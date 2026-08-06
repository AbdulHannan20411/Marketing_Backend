using Marketing.Application.Interfaces;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Maintenance;

/// <summary>
/// Removes refresh tokens that expired more than the retention window ago.
/// <para>
/// Expired tokens are spent credential material with no business value. Left in place they grow
/// the table without bound and keep the hot index for the refresh path - a lookup by token hash -
/// larger than it needs to be.
/// </para>
/// </summary>
[ScheduledJob(
    Key = "refresh-token-cleanup",
    Group = "maintenance",
    // 03:15 UTC daily: outside the traffic peak, and deliberately not on the hour, where it would
    // contend with every other system's scheduled work.
    Cron = "0 15 3 * * ?",
    Description = "Deletes refresh tokens past their retention window.")]
public sealed class RefreshTokenCleanupJob : ScheduledJobBase
{
    private readonly ISessionMaintenanceService _sessions;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="sessions">Session maintenance service.</param>
    /// <param name="logger">Logger.</param>
    public RefreshTokenCleanupJob(
        ISessionMaintenanceService sessions,
        ILogger<RefreshTokenCleanupJob> logger)
        : base(logger)
    {
        _sessions = sessions;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _sessions.PurgeExpiredSessionsAsync(cancellationToken);
}
