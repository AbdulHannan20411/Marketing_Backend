using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Auth;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using static Marketing.Common.Constants.AppConstants;
using Marketing.Common.Exceptions;
using Marketing.Common.Extensions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Marketing.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Application.Services;

/// <summary>Sign-in, refresh-token rotation and session revocation.</summary>
public sealed partial class AuthenticationService : IAuthenticationService
{
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IRequestContext _requestContext;
    private readonly ICurrentUser _currentUser;
    private readonly AuthenticationPolicyOptions _policy;
    private readonly ILogger<AuthenticationService> _logger;

    /// <summary>
    /// A valid hash of a value nobody knows, verified against when the address is unknown.
    /// <para>
    /// Without it, a request for a non-existent account returns as soon as the lookup misses, while
    /// a real account pays for a full key derivation. That difference is measurable over the
    /// network and turns the login endpoint into a user enumeration oracle - which matters more
    /// here than usual, because addresses are unique platform-wide.
    /// </para>
    /// </summary>
    private readonly Lazy<string> _decoyHash;

    /// <summary>Initialises a new instance.</summary>
    public AuthenticationService(
        IUserRepository userRepository,
        IRefreshTokenRepository refreshTokenRepository,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        IRequestContext requestContext,
        ICurrentUser currentUser,
        IOptions<AuthenticationPolicyOptions> policy,
        ILogger<AuthenticationService> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _userRepository = userRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _requestContext = requestContext;
        _currentUser = currentUser;
        _policy = policy.Value;
        _logger = logger;

        _decoyHash = new Lazy<string>(() => _passwordHasher.Hash(Guid.NewGuid().ToString("N")));
    }

    /// <inheritdoc />
    public async Task<AuthenticationResponse> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var utcNow = _dateTimeProvider.UtcNow;
        var normalizedEmail = request.Email.ToNormalisedEmail();

        var user = await _userRepository.FindForAuthenticationAsync(normalizedEmail, cancellationToken);

        if (user is null)
        {
            _passwordHasher.Verify(request.Password, _decoyHash.Value);

            LogUnknownAddressRejected(_requestContext.CorrelationId);

            throw new AuthenticationException();
        }

        if (user.LockoutEndsOn is { } lockoutEnd && lockoutEnd > utcNow)
        {
            LogLockedAccountRejected(user.Id, lockoutEnd);

            throw new AuthenticationException("account_locked");
        }

        var (isValid, requiresRehash) = _passwordHasher.Verify(request.Password, user.PasswordHash);

        if (!isValid)
        {
            // The user instance is already tracked by the sign-in lookup, so the failure counter is
            // updated in place rather than re-fetching the row.
            await RecordFailedAttemptAsync(user, utcNow, cancellationToken);
            throw new AuthenticationException();
        }

        EnsureAccountUsable(user);

        if (requiresRehash)
        {
            // The stored hash predates the current work factor. Sign-in is the only moment the
            // plaintext is available, so upgrading here is the only way to migrate without asking
            // every user to reset their password.
            user.PasswordHash = _passwordHasher.Hash(request.Password);
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEndsOn = null;
        user.LastLoginOn = utcNow;

        var response = IssueSession(user, Guid.NewGuid());

        await EnforceSessionLimitAsync(user.Id, utcNow, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogSignInSucceeded(user.Id, _requestContext.CorrelationId);

        return response;
    }

    /// <inheritdoc />
    public async Task<AuthenticationResponse> RefreshAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var utcNow = _dateTimeProvider.UtcNow;
        var presentedHash = _tokenService.HashRefreshToken(request.RefreshToken);

        var stored = await _refreshTokenRepository.FindByHashAsync(presentedHash, cancellationToken)
                     ?? throw new AuthenticationException("invalid_refresh_token");

        // A token that was already rotated or revoked is being replayed. Either the client is
        // buggy or the token was stolen; both are handled the same way, by tearing down every
        // session for the user rather than only refusing this one request.
        if (stored.ConsumedOn is not null || stored.RevokedOn is not null)
        {
            await _refreshTokenRepository.RevokeAllForUserAsync(
                stored.UserId,
                "Replay of a rotated refresh token detected.",
                utcNow,
                cancellationToken);

            LogRefreshTokenReplayDetected(stored.UserId, stored.SessionId);

            throw new AuthenticationException("refresh_token_replayed");
        }

        if (stored.ExpiresOn <= utcNow)
        {
            throw new AuthenticationException("refresh_token_expired");
        }

        var user = await _userRepository.FindWithRolesAsync(stored.UserId, cancellationToken)
                   ?? throw new AuthenticationException("invalid_refresh_token");

        // The stamp changes whenever credentials or roles change, so a session opened before that
        // change stops working immediately instead of surviving until its own expiry.
        if (user.SecurityStamp != stored.SecurityStamp)
        {
            throw new AuthenticationException("security_stamp_changed");
        }

        EnsureAccountUsable(user);

        var rotated = IssueSession(user, stored.SessionId);

        // Already tracked by the hash lookup - calling Update() would mark every column dirty and
        // rewrite the whole row for a two-field change.
        stored.ConsumedOn = utcNow;
        stored.ReplacedByTokenHash = _tokenService.HashRefreshToken(rotated.RefreshToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return rotated;
    }

    /// <inheritdoc />
    public async Task LogoutAsync(RevokeTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var presentedHash = _tokenService.HashRefreshToken(request.RefreshToken);
        var stored = await _refreshTokenRepository.FindByHashAsync(presentedHash, cancellationToken);

        // Signing out is idempotent and never reports whether the token existed. A 200 for an
        // unknown token is correct: the caller's session is not active either way, and saying
        // otherwise would confirm a token's existence to whoever holds it.
        if (stored is null || stored.RevokedOn is not null)
        {
            return;
        }

        stored.RevokedOn = _dateTimeProvider.UtcNow;
        stored.RevokedReason = "Signed out.";

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogoutEverywhereAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId
                     ?? throw new AuthenticationException("not_authenticated");

        var utcNow = _dateTimeProvider.UtcNow;

        var user = await _userRepository.GetForUpdateAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        // Rotating the stamp is what makes this take effect for tokens already in flight; revoking
        // the rows alone would leave unexpired access tokens usable until they lapsed.
        user.SecurityStamp = Guid.NewGuid();

        await _refreshTokenRepository.RevokeAllForUserAsync(
            userId,
            "Signed out of all sessions.",
            utcNow,
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogAllSessionsRevoked(userId);
    }

    /// <inheritdoc />
    public async Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId
                     ?? throw new AuthenticationException("not_authenticated");

        var user = await _userRepository.FindWithRolesAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        return BuildProfile(user);
    }

    /// <summary>Mints a token pair for a user and stages the refresh-token row.</summary>
    private AuthenticationResponse IssueSession(User user, Guid sessionId)
    {
        var roles = user.UserRoles.Select(userRole => userRole.Role.Name).Distinct(StringComparer.Ordinal).ToList();

        var permissions = user.UserRoles
            .SelectMany(userRole => userRole.Role.Permissions)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var accessToken = _tokenService.CreateAccessToken(new TokenSubject(
            user.Id,
            user.Email,
            user.DisplayName,
            user.TenantId,
            user.Tenant?.Slug,
            sessionId,
            roles,
            permissions));

        var refreshToken = _tokenService.CreateRefreshToken();

        _refreshTokenRepository.Add(new RefreshToken
        {
            Id = SequentialGuid.Create(),
            // Stamped explicitly rather than left to the ambient tenant: sign-in runs without an
            // authenticated tenant, so the interceptor has nothing to copy from.
            TenantId = user.TenantId,
            UserId = user.Id,
            SessionId = sessionId,
            TokenHash = refreshToken.Hash,
            SecurityStamp = user.SecurityStamp,
            ExpiresOn = refreshToken.ExpiresAtUtc,
            CreatedByIp = _requestContext.IpAddress,
            UserAgent = Truncate(_requestContext.UserAgent, 512),
        });

        return new AuthenticationResponse(
            accessToken.Value,
            accessToken.ExpiresAtUtc,
            refreshToken.Value,
            refreshToken.ExpiresAtUtc,
            BuildProfile(user, roles, permissions));
    }

    private async Task RecordFailedAttemptAsync(User user, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        user.FailedLoginAttempts++;

        if (user.FailedLoginAttempts >= _policy.MaxFailedLoginAttempts)
        {
            user.LockoutEndsOn = utcNow.AddMinutes(_policy.LockoutDurationMinutes);

            // Reset rather than left at the threshold, so the counter measures attempts since the
            // last lockout instead of locking on every subsequent failure forever.
            user.FailedLoginAttempts = 0;

            LogAccountLockedOut(user.Id, user.LockoutEndsOn, _policy.MaxFailedLoginAttempts);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Revokes the oldest sessions once a user exceeds the configured concurrent-session budget.
    /// </summary>
    private async Task EnforceSessionLimitAsync(Guid userId, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        var active = await _refreshTokenRepository.GetActiveSessionsAsync(userId, utcNow, cancellationToken);

        if (active.Count <= _policy.MaxConcurrentSessions)
        {
            return;
        }

        foreach (var token in active.Skip(_policy.MaxConcurrentSessions))
        {
            token.RevokedOn = utcNow;
            token.RevokedReason = "Concurrent session limit exceeded.";
        }
    }

    /// <summary>Rejects accounts and tenants that are not in a state that permits sign-in.</summary>
    private static void EnsureAccountUsable(User user)
    {
        // Every branch throws the same generic error. Distinguishing "disabled" from "wrong
        // password" would confirm the address is registered.
        if (user.Status != UserStatus.Active)
        {
            throw new AuthenticationException("account_not_active");
        }

        if (user.Tenant is { } tenant && tenant.Status != TenantStatus.Active)
        {
            throw new AuthenticationException("tenant_not_active");
        }
    }

    private static CurrentUserResponse BuildProfile(User user) =>
        BuildProfile(
            user,
            [.. user.UserRoles.Select(userRole => userRole.Role.Name).Distinct(StringComparer.Ordinal)],
            [.. user.UserRoles.SelectMany(userRole => userRole.Role.Permissions).Distinct(StringComparer.Ordinal)]);

    private static CurrentUserResponse BuildProfile(
        User user,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissions) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Tenant?.Name,
            roles.Contains(Roles.SuperAdmin, StringComparer.Ordinal),
            roles,
            permissions);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    // Source-generated logging: allocation-free, checks IsEnabled before touching arguments, and
    // gives each security-relevant event a stable EventId that alerting can key on.
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Sign-in rejected for an unknown address. CorrelationId: {CorrelationId}")]
    private partial void LogUnknownAddressRejected(string correlationId);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Sign-in rejected for locked account {UserId}. Lockout ends {LockoutEndsOn}.")]
    private partial void LogLockedAccountRejected(Guid userId, DateTimeOffset lockoutEndsOn);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "User {UserId} signed in. CorrelationId: {CorrelationId}")]
    private partial void LogSignInSucceeded(Guid userId, string correlationId);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Refresh-token replay detected for user {UserId}, session {SessionId}. All sessions revoked.")]
    private partial void LogRefreshTokenReplayDetected(Guid userId, Guid sessionId);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "All sessions revoked for user {UserId}.")]
    private partial void LogAllSessionsRevoked(Guid userId);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "User {UserId} locked out until {LockoutEndsOn} after {Attempts} failed attempts.")]
    private partial void LogAccountLockedOut(Guid userId, DateTimeOffset? lockoutEndsOn, int attempts);
}
