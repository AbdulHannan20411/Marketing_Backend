using System.Net.Mail;
using System.Net;
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
    private readonly IUserTokenRepository _userTokens;
    private readonly IAccountActivationService _activation;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly IEmailSender _emailSender;
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
        IUserTokenRepository userTokens,
        IAccountActivationService activation,
        IPasswordPolicy passwordPolicy,
        IEmailSender emailSender,
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
        _userTokens = userTokens;
        _activation = activation;
        _passwordPolicy = passwordPolicy;
        _emailSender = emailSender;
        _policy = policy.Value;
        _logger = logger;

        _decoyHash = new Lazy<string>(() => _passwordHasher.Hash(Guid.NewGuid().ToString("N")));
    }

    /// <inheritdoc />
    public async Task<AuthTokens> LoginAsync(
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
        EnsurePortalMatches(user, request.Portal);

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
    public async Task<AuthTokens> RefreshAsync(
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
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        // The session comes from the access token's sid claim, not from a body the caller controls.
        // Accepting a refresh token here would let anyone holding one end someone else's session.
        if (_currentUser.SessionId is not { } sessionId)
        {
            return;
        }

        var tokens = await _refreshTokenRepository.FindBySessionAsync(sessionId, cancellationToken);
        var utcNow = _dateTimeProvider.UtcNow;
        var revoked = 0;

        foreach (var token in tokens.Where(token => token.RevokedOn is null))
        {
            token.RevokedOn = utcNow;
            token.RevokedReason = "Signed out.";
            revoked++;
        }

        // Idempotent, and never reports whether the session existed: the caller is signed out
        // either way, and saying otherwise discloses session state.
        if (revoked > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
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
    public async Task ForgotPasswordAsync(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedEmail = request.Email.ToNormalisedEmail();
        var user = await _userRepository.FindForAuthenticationAsync(normalizedEmail, cancellationToken);

        // Same observable outcome either way - no exception, no timing branch worth measuring -
        // so the endpoint cannot be used to discover which addresses are registered.
        if (user is null)
        {
            LogPasswordResetRequestedForUnknownAddress(_requestContext.CorrelationId);
            return;
        }

        // Only usable accounts get a link. Sending one to a disabled account would let a
        // dismissed employee restore their own access.
        if (user.Status is AppConstants.UserStatus.Disabled)
        {
            LogPasswordResetRequestedForUnknownAddress(_requestContext.CorrelationId);
            return;
        }

        await _activation.SendPasswordResetAsync(user, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogPasswordResetRequested(user.Id, _requestContext.CorrelationId);
    }

    /// <inheritdoc />
    public async Task<AuthTokens> AcceptInvitationAsync(
        AcceptInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _passwordPolicy.Validate(request.Password);

        var (token, user) = await RedeemAsync(
            request.Token,
            AppConstants.UserTokenPurpose.Invitation,
            cancellationToken);

        user.PasswordHash = _passwordHasher.Hash(request.Password);
        user.Status = AppConstants.UserStatus.Active;

        // Redeeming a token sent to the address proves the user controls it, so confirmation is
        // implied rather than demanded again.
        user.EmailConfirmed = true;
        user.SecurityStamp = Guid.NewGuid();
        user.LastLoginOn = _dateTimeProvider.UtcNow;

        token.ConsumedOn = _dateTimeProvider.UtcNow;

        // Accepting the invitation *is* completing onboarding, which is what Pending means. The
        // organisation is created Pending because nobody had accepted yet; without this the very
        // first administrator is refused for a state only their own acceptance can clear, and the
        // invitation flow is a dead end unless a platform administrator flips the status by hand.
        //
        // Only from Pending. A tenant that was suspended or cancelled stays that way — an
        // outstanding invitation link must never resurrect an organisation somebody switched off.
        if (user.Tenant is { Status: TenantStatus.Pending } pending)
        {
            pending.Status = TenantStatus.Active;
        }

        EnsureAccountUsable(user);

        var tokens = IssueSession(user, Guid.NewGuid());

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogInvitationAccepted(user.Id, _requestContext.CorrelationId);

        return tokens;
    }

    /// <inheritdoc />
    public async Task ResetPasswordAsync(
        ResetPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _passwordPolicy.Validate(request.Password);

        var (token, user) = await RedeemAsync(
            request.Token,
            AppConstants.UserTokenPurpose.PasswordReset,
            cancellationToken);

        var utcNow = _dateTimeProvider.UtcNow;

        user.PasswordHash = _passwordHasher.Hash(request.Password);
        user.SecurityStamp = Guid.NewGuid();

        // A reset also clears a lockout. Someone locked out after failed attempts has just proved
        // control of the address, which is stronger evidence than waiting out the timer.
        user.FailedLoginAttempts = 0;
        user.LockoutEndsOn = null;

        token.ConsumedOn = utcNow;

        // Every session goes. If the account was compromised, leaving the attacker signed in
        // would make the reset cosmetic.
        await _refreshTokenRepository.RevokeAllForUserAsync(
            user.Id,
            "Password reset.",
            utcNow,
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogPasswordReset(user.Id, _requestContext.CorrelationId);
    }

    /// <inheritdoc />
    public async Task<CurrentUserResponse> UpdateProfileAsync(
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        var user = await _userRepository.GetForUpdateAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        // Proved once, up front, for the whole request. Changing the address on an account is an
        // account-takeover step - whoever controls the address controls password resets - so a
        // session left open on a shared machine must not be enough on its own. Renaming yourself is
        // not dangerous and is deliberately not gated: asking for a password to change a display
        // name teaches people to type it without thinking.
        if (request.RequiresPassword)
        {
            var (isValid, _) = _passwordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash);

            if (!isValid)
            {
                throw new ValidationException(
                    nameof(request.CurrentPassword),
                    "That is not your current password.");
            }
        }

        var previousEmail = user.Email;
        var emailChanged = false;

        if (request.DisplayName is not null)
        {
            var displayName = request.DisplayName.Trim();

            if (displayName.Length == 0)
            {
                throw new ValidationException(nameof(request.DisplayName), "Enter your name.");
            }

            if (displayName.Length > 120)
            {
                throw new ValidationException(nameof(request.DisplayName), "That name is too long.");
            }

            user.DisplayName = displayName;
        }

        if (request.Email is not null)
        {
            var email = request.Email.Trim();

            if (!MailAddress.TryCreate(email, out _))
            {
                throw new ValidationException(nameof(request.Email), "Enter a valid email address.");
            }

            var normalized = email.ToLowerInvariant();

            if (!string.Equals(normalized, user.NormalizedEmail, StringComparison.Ordinal))
            {
                if (await _userRepository.IsEmailTakenAsync(normalized, user.Id, cancellationToken))
                {
                    throw new BusinessRuleException(
                        "email_in_use",
                        "Another account already uses that address.");
                }

                user.Email = email;
                user.NormalizedEmail = normalized;
                emailChanged = true;
            }
        }

        if (request.NewPassword is not null)
        {
            _passwordPolicy.Validate(request.NewPassword, nameof(request.NewPassword));

            user.PasswordHash = _passwordHasher.Hash(request.NewPassword);

            await RevokeOtherSessionsAsync(user, "Password changed.", cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // After the commit, and best-effort. The old address is the only place a takeover can be
        // reported from, which makes this the single most valuable safeguard here - but a bounced
        // warning must not roll back a change the user asked for and has already been told about.
        if (emailChanged)
        {
            await NotifyAddressChangedAsync(user, previousEmail, cancellationToken);
        }

        LogProfileUpdated(user.Id, emailChanged, request.NewPassword is not null);

        return await GetCurrentUserAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OnboardingStateResponse> GetOnboardingStateAsync(
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        var user = await _userRepository.GetForUpdateAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        // Never a 404. A user with nothing stored is not-started at step zero, which is what the
        // column defaults to - so first sign-in needs no special case here or in the client.
        return new OnboardingStateResponse(
            user.OnboardingStatus,
            user.OnboardingStepIndex,
            user.OnboardingUpdatedOn);
    }

    /// <inheritdoc />
    public async Task<OnboardingStateResponse> UpdateOnboardingStateAsync(
        UpdateOnboardingStateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        var user = await _userRepository.GetForUpdateAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        user.OnboardingStatus = request.Status;

        // Clamped rather than validated. The server cannot know how many steps this user's tour has
        // - the list is derived from their own navigation - so the only wrong value it can rule out
        // is a negative one, and refusing the write would lose the status alongside it.
        user.OnboardingStepIndex = Math.Max(0, request.StepIndex);
        user.OnboardingUpdatedOn = _dateTimeProvider.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new OnboardingStateResponse(
            user.OnboardingStatus,
            user.OnboardingStepIndex,
            user.OnboardingUpdatedOn);
    }

    /// <summary>Warns the address that was replaced, so a takeover cannot happen quietly.</summary>
    private async Task NotifyAddressChangedAsync(
        User user,
        string previousEmail,
        CancellationToken cancellationToken)
    {
        try
        {
            await _emailSender.SendAsync(
                new EmailMessage(
                    previousEmail,
                    user.DisplayName,
                    "The email address on your account was changed",
                    $"<p>Hello {WebUtility.HtmlEncode(user.DisplayName)},</p>"
                    + "<p>The email address on your account was changed to "
                    + $"<strong>{WebUtility.HtmlEncode(user.Email)}</strong>.</p>"
                    + "<p>If this was you, no action is needed. <strong>If it was not, contact "
                    + "support immediately</strong> - someone else may have access to your "
                    + "account.</p>",
                    $"Hello {user.DisplayName},\n\n"
                    + $"The email address on your account was changed to {user.Email}.\n\n"
                    + "If this was you, no action is needed. If it was not, contact support "
                    + "immediately - someone else may have access to your account."),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAddressChangeWarningFailed(exception, user.Id);
        }
    }

    /// <summary>
    /// Ends every session but the caller's own.
    /// </summary>
    /// <remarks>
    /// Signing someone out of the tab they just used to change their password is hostile and
    /// teaches nothing, so that one session is re-stamped and survives the rotation.
    /// </remarks>
    private async Task RevokeOtherSessionsAsync(User user, string reason, CancellationToken cancellationToken)
    {
        var utcNow = _dateTimeProvider.UtcNow;
        var currentSessionId = _currentUser.SessionId;

        user.SecurityStamp = Guid.NewGuid();

        var sessions = await _refreshTokenRepository.GetActiveSessionsAsync(user.Id, utcNow, cancellationToken);

        foreach (var session in sessions)
        {
            if (currentSessionId is { } keep && session.SessionId == keep)
            {
                session.SecurityStamp = user.SecurityStamp;
                continue;
            }

            session.RevokedOn = utcNow;
            session.RevokedReason = reason;
        }
    }

    /// <inheritdoc />
    public async Task ChangePasswordAsync(
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        var user = await _userRepository.GetForUpdateAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        var (isValid, _) = _passwordHasher.Verify(request.CurrentPassword, user.PasswordHash);

        if (!isValid)
        {
            // Not a generic sign-in failure: the caller is already authenticated, so naming the
            // wrong field is helpful and discloses nothing they do not already know.
            throw new ValidationException(nameof(request.CurrentPassword), "That is not your current password.");
        }

        _passwordPolicy.Validate(request.NewPassword, nameof(request.NewPassword));

        var utcNow = _dateTimeProvider.UtcNow;
        var currentSessionId = _currentUser.SessionId;

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        user.SecurityStamp = Guid.NewGuid();

        // Other sessions end; the caller's own survives, because signing someone out of the tab
        // they just used to change their password is hostile and teaches nothing.
        var sessions = await _refreshTokenRepository.GetActiveSessionsAsync(user.Id, utcNow, cancellationToken);

        foreach (var session in sessions)
        {
            if (currentSessionId is { } keep && session.SessionId == keep)
            {
                // Re-stamped so it survives the rotation that just invalidated everything else.
                session.SecurityStamp = user.SecurityStamp;
                continue;
            }

            session.RevokedOn = utcNow;
            session.RevokedReason = "Password changed.";
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogPasswordChanged(user.Id, _requestContext.CorrelationId);
    }

    /// <summary>
    /// Looks up and validates an emailed token.
    /// <para>
    /// Every failure - unknown, wrong purpose, expired, already used - returns the same error. The
    /// caller is unauthenticated, and distinguishing them would let someone probe which tokens
    /// exist.
    /// </para>
    /// </summary>
    private async Task<(UserToken Token, User User)> RedeemAsync(
        string presented,
        AppConstants.UserTokenPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            throw new AuthenticationException("invalid_token");
        }

        var hash = _tokenService.HashSecureToken(presented);

        // Through the repository, which ignores the tenant filter. This request is anonymous — the
        // caller is accepting an invitation or resetting a password and has no tenant yet — so a
        // token belonging to an organisation is invisible to the ordinary query and a valid link
        // comes back as "invalid_token". The hash is the credential; the tenant is what resolving
        // it tells us.
        var token = await _userTokens.FindByHashAsync(hash, cancellationToken)
                    ?? throw new AuthenticationException("invalid_token");

        if (token.Purpose != purpose || !token.IsUsable(_dateTimeProvider.UtcNow))
        {
            throw new AuthenticationException("invalid_token");
        }

        // Also filter-free, and for the same reason: the user this token belongs to sits inside the
        // tenant the anonymous caller cannot yet see.
        var user = await _userRepository.FindWithRolesAsync(token.UserId, cancellationToken)
                   ?? throw new AuthenticationException("invalid_token");

        return (token, user);
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
    private AuthTokens IssueSession(User user, Guid sessionId)
    {
        var roles = user.UserRoles.Select(userRole => userRole.Role.Name).Distinct(StringComparer.Ordinal).ToList();

        // The effective set - role defaults plus per-user overrides. The client does not derive
        // permissions from role, so the token has to carry the real answer.
        var permissions = EffectivePermissions.Resolve(roles, user.PermissionOverrides);

        var accessToken = _tokenService.CreateAccessToken(new TokenSubject(
            user.Id,
            user.Email,
            user.DisplayName,
            // One role string, as the client contract requires.
            Roles.Primary(roles),
            permissions,
            // The organisation's display name, never its identifier.
            user.Tenant?.Name,
            AvatarUrl: null,
            user.TenantId,
            user.Tenant?.Slug,
            sessionId));

        var refreshToken = _tokenService.CreateRefreshToken();

        _refreshTokenRepository.Add(new RefreshToken
        {
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

        return new AuthTokens(accessToken.Value, refreshToken.Value, accessToken.ExpiresAtUtc);
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
    private async Task EnforceSessionLimitAsync(long userId, DateTimeOffset utcNow, CancellationToken cancellationToken)
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

    /// <summary>
    /// Rejects an account signing in at the wrong entrance.
    /// <para>
    /// Platform staff belong at the super-admin portal and tenant users at the admin portal.
    /// The client already enforces this, but a client-side check is bypassable with any HTTP tool,
    /// so it is repeated here where it counts. Skipped when the caller omits the field, keeping
    /// older clients working.
    /// </para>
    /// </summary>
    private static void EnsurePortalMatches(User user, string? portal)
    {
        if (portal is null)
        {
            return;
        }

        var isPlatformStaff = user.UserRoles.Any(userRole =>
            string.Equals(userRole.Role.Name, Roles.SuperAdmin, StringComparison.Ordinal));

        var expected = isPlatformStaff ? LoginPortals.SuperAdmin : LoginPortals.Admin;

        if (!string.Equals(portal, expected, StringComparison.Ordinal))
        {
            // Generic code, like every other sign-in failure, so the response does not reveal
            // that the address exists at the other portal.
            throw new AuthenticationException("invalid_credentials");
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
    private partial void LogLockedAccountRejected(long userId, DateTimeOffset lockoutEndsOn);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "User {UserId} signed in. CorrelationId: {CorrelationId}")]
    private partial void LogSignInSucceeded(long userId, string correlationId);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Refresh-token replay detected for user {UserId}, session {SessionId}. All sessions revoked.")]
    private partial void LogRefreshTokenReplayDetected(long userId, Guid sessionId);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "All sessions revoked for user {UserId}.")]
    private partial void LogAllSessionsRevoked(long userId);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "User {UserId} locked out until {LockoutEndsOn} after {Attempts} failed attempts.")]
    private partial void LogAccountLockedOut(long userId, DateTimeOffset? lockoutEndsOn, int attempts);
    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Information,
        Message = "Password reset requested for an unknown address. CorrelationId: {CorrelationId}")]
    private partial void LogPasswordResetRequestedForUnknownAddress(string correlationId);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Information,
        Message = "Password reset requested for user {UserId}. CorrelationId: {CorrelationId}")]
    private partial void LogPasswordResetRequested(long userId, string correlationId);
    [LoggerMessage(
        EventId = 2009,
        Level = LogLevel.Information,
        Message = "User {UserId} accepted an invitation and activated their account. CorrelationId: {CorrelationId}")]
    private partial void LogInvitationAccepted(long userId, string correlationId);

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Warning,
        Message = "Password reset completed for user {UserId}; all sessions revoked. CorrelationId: {CorrelationId}")]
    private partial void LogPasswordReset(long userId, string correlationId);

    [LoggerMessage(
        EventId = 2012,
        Level = LogLevel.Information,
        Message = "User {UserId} updated their profile. Email changed: {EmailChanged}. "
                  + "Password changed: {PasswordChanged}.")]
    private partial void LogProfileUpdated(long userId, bool emailChanged, bool passwordChanged);

    [LoggerMessage(
        EventId = 2013,
        Level = LogLevel.Warning,
        Message = "Could not warn the previous address that user {UserId} changed their email.")]
    private partial void LogAddressChangeWarningFailed(Exception exception, long userId);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Information,
        Message = "User {UserId} changed their password. CorrelationId: {CorrelationId}")]
    private partial void LogPasswordChanged(long userId, string correlationId);
}
