using System.Security.Cryptography;
using System.Text;
using Marketing.Application.Configurations;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Security;

/// <summary>What a sign-in turned out to be, for the alerts that follow it.</summary>
/// <param name="SessionId">The new session.</param>
/// <param name="DeviceLabel">"Chrome / Windows".</param>
/// <param name="IpAddress">Where it came from.</param>
/// <param name="Location">City and country, when known.</param>
/// <param name="IsFirstEver">The account's first recorded sign-in, which is new by definition and alarms nobody.</param>
/// <param name="IsNewDevice">A device this account has not used before.</param>
/// <param name="IsNewLocation">A place this account has not signed in from before.</param>
/// <param name="DisplacedCount">Sessions this sign-in ended.</param>
/// <param name="DevicesInWindow">Distinct devices within the policy window, including this one.</param>
public sealed record SignInFacts(
    Guid SessionId,
    string DeviceLabel,
    string? IpAddress,
    string? Location,
    bool IsFirstEver,
    bool IsNewDevice,
    bool IsNewLocation,
    int DisplacedCount,
    int DevicesInWindow);

/// <summary>Records sessions, keeps them honest, and ends them.</summary>
public interface ISessionTracker
{
    /// <summary>
    /// Records a sign-in and the sessions it displaced. Adds rows to the unit of work; the caller saves.
    /// </summary>
    /// <param name="user">Who signed in.</param>
    /// <param name="sessionId">The new session.</param>
    /// <param name="displaced">Sessions ended by this sign-in under the one-session policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SignInFacts> StartAsync(
        User user,
        Guid sessionId,
        IReadOnlyCollection<Guid> displaced,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends whatever alerts a sign-in deserves, after it has been saved. Never throws.
    /// </summary>
    /// <param name="user">Who signed in.</param>
    /// <param name="facts">What the sign-in turned out to be.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AnnounceAsync(User user, SignInFacts facts, CancellationToken cancellationToken = default);

    /// <summary>Whether a session may still make requests. Cached briefly; called on every request.</summary>
    /// <param name="sessionId">Session from the access token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Marks a session as alive now. Returns false when it has been ended.</summary>
    /// <param name="userId">Whose session it is.</param>
    /// <param name="sessionId">Session from the access token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> TouchAsync(long userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Ends a session and every refresh token it holds, and saves.</summary>
    /// <param name="session">Session to end.</param>
    /// <param name="reason">Stable code the client can explain, such as <c>revoked_by_admin</c>.</param>
    /// <param name="revokedByUserId">Who ended it, when a person did.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RevokeAsync(
        UserSession session,
        string reason,
        long? revokedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Records a wrong password, and alerts when they pile up. Adds rows; the caller saves.</summary>
    /// <param name="user">Account whose password was wrong.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RecordFailedLoginAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every session one account has, without saving. The caller's save commits it, so it can
    /// share a transaction with whatever made it necessary.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="reason">Why, for the session rows.</param>
    /// <param name="revokedByUserId">Who ended them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RevokeAllAsync(long userId, string reason, long? revokedByUserId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISessionTracker" />
/// <remarks>
/// Liveness is read from the refresh tokens, not from this platform's session rows. Every way a
/// session can end - signing out, a password change, "sign out everywhere", a replayed token, the
/// one-session rule, an administrator - ends its refresh tokens, so asking "does this session still
/// hold a live refresh token" catches all of them, including sessions that began before session rows
/// existed. The session row adds what tokens cannot: the device, the place, and when it was last used.
/// </remarks>
public sealed partial class SessionTracker : ISessionTracker
{
    /// <summary>How long a liveness answer is reused before the database is asked again.</summary>
    /// <remarks>
    /// The whole cost of per-request checking is this query, and thirty seconds makes it one query per
    /// session per half-minute. It is also the longest a displaced session can keep working.
    /// </remarks>
    private static readonly TimeSpan LivenessCacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>Heartbeats closer together than this change nothing worth a write.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    private readonly IRepository<UserSession> _sessions;
    private readonly IRepository<SecurityEvent> _events;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRequestContext _request;
    private readonly ISecurityAlertService _alerts;
    private readonly IMemoryCache _cache;
    private readonly IDateTimeProvider _clock;
    private readonly AuthenticationPolicyOptions _policy;
    private readonly ILogger<SessionTracker> _logger;

    /// <summary>Initialises a new instance.</summary>
    public SessionTracker(
        IRepository<UserSession> sessions,
        IRepository<SecurityEvent> events,
        IRefreshTokenRepository refreshTokens,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IRequestContext request,
        ISecurityAlertService alerts,
        IMemoryCache cache,
        IDateTimeProvider clock,
        IOptions<AuthenticationPolicyOptions> policy,
        ILogger<SessionTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _sessions = sessions;
        _events = events;
        _refreshTokens = refreshTokens;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _request = request;
        _alerts = alerts;
        _cache = cache;
        _clock = clock;
        _policy = policy.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SignInFacts> StartAsync(
        User user,
        Guid sessionId,
        IReadOnlyCollection<Guid> displaced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(displaced);

        var now = _clock.UtcNow;
        var device = UserAgentParser.Parse(_request.UserAgent);
        var deviceId = DeviceIdentity(_request.DeviceId, _request.UserAgent);
        var location = _request.Location;
        var ip = _request.IpAddress;

        var history = await _queries.ToListAsync(
            _sessions.Query()
                .IgnoreQueryFilters()
                .Where(session => !session.IsDeleted && session.UserId == user.Id)
                .Select(session => new { session.DeviceId, session.Location, session.LastActivityAt }),
            cancellationToken);

        var isFirstEver = history.Count == 0;
        var isNewDevice = !isFirstEver && !history.Any(session => session.DeviceId == deviceId);
        var isNewLocation = !isFirstEver
                            && location is { Length: > 0 }
                            && !history.Any(session => string.Equals(session.Location, location, StringComparison.OrdinalIgnoreCase));

        var windowStart = now.AddDays(-_policy.DeviceWindowDays);
        var devicesInWindow = history
            .Where(session => session.LastActivityAt >= windowStart)
            .Select(session => session.DeviceId)
            .Append(deviceId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        _sessions.Add(new UserSession
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            SessionId = sessionId,
            DeviceId = deviceId,
            DeviceLabel = device.Label,
            Browser = device.Browser,
            OperatingSystem = device.OperatingSystem,
            DeviceType = device.DeviceType,
            IpAddress = ip,
            LastIpAddress = ip,
            Location = location,
            UserAgent = Truncate(_request.UserAgent, 512),
            LastActivityAt = now,
        });

        // Platform staff keep a session row, so "your devices" still lists and ends their own
        // sessions, and nothing else: no displacement, no device or location events, nothing that
        // feeds a risk score.
        if (user.TenantId is null)
        {
            return new SignInFacts(sessionId, device.Label, ip, location, false, false, false, 0, devicesInWindow);
        }

        if (displaced.Count > 0)
        {
            await MarkDisplacedAsync(user, displaced, device.Label, ip, location, now, cancellationToken);
        }

        if (isNewDevice)
        {
            Record(user, sessionId, SecurityEventKind.NewDevice, ip, location, device.Label,
                $"First sign-in from {device.Label}.", now);
        }

        if (isNewLocation)
        {
            Record(user, sessionId, SecurityEventKind.NewLocation, ip, location, device.Label,
                $"First sign-in from {location}.", now);
        }

        if (isNewDevice && devicesInWindow > _policy.MaxDevicesPerUser)
        {
            Record(user, sessionId, SecurityEventKind.DeviceLimitExceeded, ip, location, device.Label,
                $"{devicesInWindow} devices in {_policy.DeviceWindowDays} days; the limit is {_policy.MaxDevicesPerUser}.",
                now);
        }

        return new SignInFacts(
            sessionId,
            device.Label,
            ip,
            location,
            isFirstEver,
            isNewDevice,
            isNewLocation,
            displaced.Count,
            devicesInWindow);
    }

    /// <inheritdoc />
    public async Task AnnounceAsync(User user, SignInFacts facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Never about platform staff: nobody is shown, emailed or scored for their sign-ins.
        if (user.TenantId is null)
        {
            return;
        }

        try
        {
            await _alerts.AnnounceSignInAsync(user, facts, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The person is already signed in. A failed alert is logged and forgotten; turning it into
            // a failed sign-in would punish them for a mail server being slow.
            LogAnnouncementFailed(exception, user.Id);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(sessionId);

        if (_cache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        var now = _clock.UtcNow;

        // The head of the refresh-token chain: not consumed by a rotation, not revoked, not expired.
        // Exactly one exists while a session lives, and none once it has been ended by any route.
        var live = await _queries.CountAsync(
            _refreshTokens.Query()
                .IgnoreQueryFilters()
                .Where(token =>
                    !token.IsDeleted
                    && token.SessionId == sessionId
                    && token.RevokedOn == null
                    && token.ConsumedOn == null
                    && token.ExpiresOn > now),
            cancellationToken) > 0;

        _cache.Set(key, live, LivenessCacheDuration);

        return live;
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(long userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!await IsActiveAsync(sessionId, cancellationToken))
        {
            return false;
        }

        var session = await _queries.FirstOrDefaultAsync(
            _sessions.Query(asNoTracking: false)
                .IgnoreQueryFilters()
                .Where(candidate => candidate.SessionId == sessionId && candidate.UserId == userId),
            cancellationToken);

        var now = _clock.UtcNow;

        // A session that began before session rows existed is alive but unrecorded. It is recorded now,
        // from what this request knows, so it appears in the device list rather than being invisible.
        if (session is null)
        {
            var device = UserAgentParser.Parse(_request.UserAgent);

            _sessions.Add(new UserSession
            {
                TenantId = await TenantOfAsync(sessionId, cancellationToken),
                UserId = userId,
                SessionId = sessionId,
                DeviceId = DeviceIdentity(_request.DeviceId, _request.UserAgent),
                DeviceLabel = device.Label,
                Browser = device.Browser,
                OperatingSystem = device.OperatingSystem,
                DeviceType = device.DeviceType,
                IpAddress = _request.IpAddress,
                LastIpAddress = _request.IpAddress,
                Location = _request.Location,
                UserAgent = Truncate(_request.UserAgent, 512),
                LastActivityAt = now,
            });

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return true;
        }

        if (session.RevokedAt is not null)
        {
            return false;
        }

        if (now - session.LastActivityAt < TouchInterval && session.LastIpAddress == _request.IpAddress)
        {
            return true;
        }

        session.LastActivityAt = now;
        session.LastIpAddress = _request.IpAddress ?? session.LastIpAddress;
        session.Location = _request.Location ?? session.Location;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        UserSession session,
        string reason,
        long? revokedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var now = _clock.UtcNow;

        session.RevokedAt ??= now;
        session.RevokedReason ??= reason;
        session.RevokedByUserId ??= revokedByUserId;

        var tokens = await _refreshTokens.FindBySessionAsync(session.SessionId, cancellationToken);

        foreach (var token in tokens.Where(token => token.RevokedOn is null))
        {
            token.RevokedOn = now;
            token.RevokedReason = reason;
        }

        if (revokedByUserId is { } admin)
        {
            Record(session.UserId, session.TenantId, session.SessionId, SecurityEventKind.SessionRevoked,
                session.LastIpAddress, session.Location, session.DeviceLabel,
                $"Ended by user {admin}.", now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Dropped from the cache so the device is signed out on its next request, not in thirty seconds.
        _cache.Remove(CacheKey(session.SessionId));
    }

    /// <inheritdoc />
    public async Task RevokeAllAsync(
        long userId,
        string reason,
        long? revokedByUserId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var open = await _queries.ToListAsync(
            _sessions.Query(asNoTracking: false)
                .IgnoreQueryFilters()
                .Where(session => !session.IsDeleted && session.UserId == userId && session.RevokedAt == null),
            cancellationToken);

        foreach (var session in open)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
            session.RevokedByUserId = revokedByUserId;

            // So the device is refused on its next request rather than when the cached answer lapses.
            _cache.Remove(CacheKey(session.SessionId));
        }

        // One statement for every refresh token, including any from a session with no row.
        await _refreshTokens.RevokeAllForUserAsync(userId, reason, now, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordFailedLoginAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Lockout still applies to platform staff - that is the authentication service's job and runs
        // regardless. What is skipped is the security event and the alert built on it.
        if (user.TenantId is null)
        {
            return;
        }

        var now = _clock.UtcNow;

        Record(user, null, SecurityEventKind.FailedLogins, _request.IpAddress, _request.Location,
            UserAgentParser.Parse(_request.UserAgent).Label, "Wrong password.", now);

        var recent = await _queries.CountAsync(
            _events.Query()
                .IgnoreQueryFilters()
                .Where(securityEvent =>
                    !securityEvent.IsDeleted
                    && securityEvent.UserId == user.Id
                    && securityEvent.Kind == SecurityEventKind.FailedLogins
                    && securityEvent.OccurredAt >= now.AddMinutes(-15)),
            cancellationToken);

        try
        {
            // Counted before this attempt is saved, so the fifth failure is the one that finds four.
            await _alerts.AnnounceFailedLoginsAsync(user, recent + 1, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAnnouncementFailed(exception, user.Id);
        }
    }

    /// <summary>Records who was pushed off, and marks their session rows ended.</summary>
    private async Task MarkDisplacedAsync(
        User user,
        IReadOnlyCollection<Guid> displaced,
        string byDevice,
        string? ip,
        string? location,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = await _queries.ToListAsync(
            _sessions.Query(asNoTracking: false)
                .IgnoreQueryFilters()
                .Where(session => displaced.Contains(session.SessionId)),
            cancellationToken);

        foreach (var sessionId in displaced)
        {
            var row = rows.FirstOrDefault(session => session.SessionId == sessionId);

            if (row is not null)
            {
                row.RevokedAt ??= now;
                row.RevokedReason ??= "signed_in_elsewhere";
            }

            Record(user, sessionId, SecurityEventKind.SessionDisplaced, ip, location, byDevice,
                $"Signed out by a sign-in from {byDevice}" + (location is { Length: > 0 } ? $" in {location}." : "."),
                now);

            _cache.Remove(CacheKey(sessionId));
        }
    }

    private void Record(
        User user,
        Guid? sessionId,
        SecurityEventKind kind,
        string? ip,
        string? location,
        string? deviceLabel,
        string detail,
        DateTimeOffset now) =>
        Record(user.Id, user.TenantId, sessionId, kind, ip, location, deviceLabel, detail, now);

    private void Record(
        long userId,
        long? tenantId,
        Guid? sessionId,
        SecurityEventKind kind,
        string? ip,
        string? location,
        string? deviceLabel,
        string detail,
        DateTimeOffset now) =>
        _events.Add(new SecurityEvent
        {
            TenantId = tenantId,
            UserId = userId,
            SessionId = sessionId,
            Kind = kind,
            IpAddress = ip,
            Location = location,
            DeviceLabel = deviceLabel,
            Detail = detail.Length <= 500 ? detail : detail[..500],
            OccurredAt = now,
        });

    private async Task<long?> TenantOfAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await _queries.FirstOrDefaultAsync(
            _refreshTokens.Query()
                .IgnoreQueryFilters()
                .Where(token => token.SessionId == sessionId)
                .Select(token => token.TenantId),
            cancellationToken);

    /// <summary>
    /// The device a request came from: the browser's own identifier when it sent one, otherwise a
    /// fingerprint of its user agent.
    /// </summary>
    private static string DeviceIdentity(string? deviceId, string? userAgent)
    {
        if (deviceId is { Length: > 0 })
        {
            return deviceId;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userAgent ?? "unknown"));

        return "ua-" + Convert.ToHexStringLower(hash)[..32];
    }

    private static string CacheKey(Guid sessionId) => $"session-live:{sessionId:N}";

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    [LoggerMessage(
        EventId = 2860,
        Level = LogLevel.Warning,
        Message = "Security alerts for user {UserId} could not be sent; the sign-in itself succeeded.")]
    private partial void LogAnnouncementFailed(Exception exception, long userId);
}
