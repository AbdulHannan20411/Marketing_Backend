using System.Globalization;
using Marketing.Application.Configurations;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Security;

/// <summary>The facts about an account's recent sign-ins that sharing tends to leave behind.</summary>
/// <param name="DevicesInWindow">Distinct devices used within the device window.</param>
/// <param name="DeviceLimit">How many devices the workspace policy allows before it is unusual.</param>
/// <param name="DeviceWindowDays">How far back devices are counted.</param>
/// <param name="ActiveSessions">Sessions alive right now.</param>
/// <param name="DisplacedLast24Hours">Times a sign-in elsewhere ended one of this account's sessions.</param>
/// <param name="DistinctIpsLast24Hours">Addresses seen in the last day.</param>
/// <param name="DistinctLocationsLast7Days">Cities or countries seen in the last week.</param>
/// <param name="FailedLoginsLast24Hours">Wrong passwords in the last day.</param>
/// <param name="NewDeviceLast24Hours">Whether a device the account never used before appeared today.</param>
public sealed record RiskSignals(
    int DevicesInWindow,
    int DeviceLimit,
    int DeviceWindowDays,
    int ActiveSessions,
    int DisplacedLast24Hours,
    int DistinctIpsLast24Hours,
    int DistinctLocationsLast7Days,
    int FailedLoginsLast24Hours,
    bool NewDeviceLast24Hours);

/// <summary>How likely an account is to be shared, and why.</summary>
/// <param name="Level">Low, medium or high.</param>
/// <param name="Score">The points behind the level, for sorting a list of accounts.</param>
/// <param name="Reasons">Each signal that contributed, in plain words.</param>
public sealed record RiskAssessment(RiskLevel Level, int Score, IReadOnlyList<string> Reasons);

/// <summary>Turns sign-in signals into a risk level with its reasons.</summary>
/// <remarks>
/// Additive and explainable on purpose. A Super Admin acting on this has to be able to say why an
/// account was flagged, and a customer disputing it deserves the same answer; a weighted sum with a
/// reason per term gives both, where anything cleverer would not.
/// <para>
/// With one session per account enforced, several people cannot be signed in at once - they sign
/// each other out instead. Displacement is therefore the heaviest signal: it is the trace sharing
/// leaves when sharing is prevented.
/// </para>
/// </remarks>
public static class RiskScorer
{
    /// <summary>Score at which an account is high risk.</summary>
    public const int HighThreshold = 60;

    /// <summary>Score at which an account is worth a look.</summary>
    public const int MediumThreshold = 30;

    /// <summary>Scores a set of signals.</summary>
    /// <param name="signals">What the account's recent sign-ins look like.</param>
    public static RiskAssessment Score(RiskSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var score = 0;
        var reasons = new List<string>();

        void Add(int points, string reason)
        {
            score += points;
            reasons.Add(reason);
        }

        switch (signals.DisplacedLast24Hours)
        {
            case >= 6:
                Add(45, $"{Count(signals.DisplacedLast24Hours)} sign-ins pushed another session off in 24 hours");
                break;
            case >= 3:
                Add(30, $"{Count(signals.DisplacedLast24Hours)} sign-ins pushed another session off in 24 hours");
                break;
            case >= 1:
                Add(10, "A sign-in pushed another session off in the last 24 hours");
                break;
        }

        if (signals.DevicesInWindow > signals.DeviceLimit)
        {
            Add(25, $"{Count(signals.DevicesInWindow)} devices in {Count(signals.DeviceWindowDays)} days");
        }
        else if (signals.DevicesInWindow == signals.DeviceLimit && signals.DeviceLimit > 1)
        {
            Add(10, $"{Count(signals.DevicesInWindow)} devices in {Count(signals.DeviceWindowDays)} days");
        }

        // Only possible when single sessions are switched off, and then it is the plainest signal.
        if (signals.ActiveSessions >= 2)
        {
            Add(20, $"{Count(signals.ActiveSessions)} simultaneous sessions");
        }

        if (signals.DistinctLocationsLast7Days >= 2)
        {
            Add(20, $"{Count(signals.DistinctLocationsLast7Days)} locations in 7 days");
        }

        if (signals.DistinctIpsLast24Hours >= 3)
        {
            Add(10, $"{Count(signals.DistinctIpsLast24Hours)} IP addresses in 24 hours");
        }

        if (signals.FailedLoginsLast24Hours >= 5)
        {
            Add(10, $"{Count(signals.FailedLoginsLast24Hours)} failed sign-ins in 24 hours");
        }

        if (signals.NewDeviceLast24Hours)
        {
            Add(5, "New device in the last 24 hours");
        }

        var level = score >= HighThreshold ? RiskLevel.High
            : score >= MediumThreshold ? RiskLevel.Medium
            : RiskLevel.Low;

        return new RiskAssessment(level, score, reasons);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Gathers an account's risk signals and scores them.</summary>
public interface IAccountRiskEvaluator
{
    /// <summary>Assesses one account.</summary>
    /// <param name="userId">Account to assess.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<RiskAssessment> EvaluateAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Reads the raw signals, for a screen that shows them alongside the verdict.</summary>
    /// <param name="userId">Account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<RiskSignals> SignalsAsync(long userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountRiskEvaluator" />
/// <remarks>
/// Reads across tenants: it is used by the sign-in path, where the account's own tenant is not yet an
/// ambient context, and by the Super Admin screen, which sees every tenant by design.
/// </remarks>
public sealed class AccountRiskEvaluator : IAccountRiskEvaluator
{
    private readonly IRepository<UserSession> _sessions;
    private readonly IRepository<SecurityEvent> _events;
    private readonly IQueryExecutor _queries;
    private readonly IDateTimeProvider _clock;
    private readonly AuthenticationPolicyOptions _policy;

    /// <summary>Initialises a new instance.</summary>
    public AccountRiskEvaluator(
        IRepository<UserSession> sessions,
        IRepository<SecurityEvent> events,
        IQueryExecutor queries,
        IDateTimeProvider clock,
        IOptions<AuthenticationPolicyOptions> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _sessions = sessions;
        _events = events;
        _queries = queries;
        _clock = clock;
        _policy = policy.Value;
    }

    /// <inheritdoc />
    public async Task<RiskAssessment> EvaluateAsync(long userId, CancellationToken cancellationToken = default) =>
        RiskScorer.Score(await SignalsAsync(userId, cancellationToken));

    /// <inheritdoc />
    public async Task<RiskSignals> SignalsAsync(long userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var dayAgo = now.AddDays(-1);
        var weekAgo = now.AddDays(-7);
        var windowStart = now.AddDays(-_policy.DeviceWindowDays);
        var activeSince = now.AddMinutes(-_policy.ActiveWindowMinutes);

        // One read of the account's recent sessions answers four of the questions; counting in memory
        // keeps it to one round trip for what is, per person, a handful of rows.
        var sessions = await _queries.ToListAsync(
            _sessions.Query()
                .IgnoreQueryFilters()
                .Where(session => !session.IsDeleted && session.UserId == userId && session.LastActivityAt >= windowStart)
                .Select(session => new
                {
                    session.DeviceId,
                    session.IpAddress,
                    session.LastIpAddress,
                    session.Location,
                    session.CreatedOn,
                    session.LastActivityAt,
                    session.RevokedAt,
                }),
            cancellationToken);

        var events = await _queries.ToListAsync(
            _events.Query()
                .IgnoreQueryFilters()
                .Where(securityEvent =>
                    !securityEvent.IsDeleted
                    && securityEvent.UserId == userId
                    && securityEvent.OccurredAt >= dayAgo
                    && (securityEvent.Kind == SecurityEventKind.SessionDisplaced
                        || securityEvent.Kind == SecurityEventKind.FailedLogins
                        || securityEvent.Kind == SecurityEventKind.NewDevice))
                .Select(securityEvent => securityEvent.Kind),
            cancellationToken);

        var ipsToday = sessions
            .Where(session => session.LastActivityAt >= dayAgo)
            .SelectMany(session => new[] { session.IpAddress, session.LastIpAddress })
            .Where(ip => ip is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new RiskSignals(
            DevicesInWindow: sessions.Select(session => session.DeviceId).Distinct(StringComparer.Ordinal).Count(),
            DeviceLimit: _policy.MaxDevicesPerUser,
            DeviceWindowDays: _policy.DeviceWindowDays,
            ActiveSessions: sessions.Count(session => session.RevokedAt == null && session.LastActivityAt >= activeSince),
            DisplacedLast24Hours: events.Count(kind => kind == SecurityEventKind.SessionDisplaced),
            DistinctIpsLast24Hours: ipsToday,
            DistinctLocationsLast7Days: sessions
                .Where(session => session.LastActivityAt >= weekAgo && session.Location is { Length: > 0 })
                .Select(session => session.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            FailedLoginsLast24Hours: events.Count(kind => kind == SecurityEventKind.FailedLogins),
            NewDeviceLast24Hours: events.Contains(SecurityEventKind.NewDevice));
    }
}
