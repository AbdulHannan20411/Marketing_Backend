using Marketing.Application.Services.Exports;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Maintenance;

/// <summary>
/// Expires old export files, and requeues exports nobody picked up.
/// </summary>
/// <remarks>
/// Two jobs in one trigger because they are the same sweep over the same table and neither is
/// worth its own schedule.
/// <para>
/// The second half is what keeps the database honest as the record of the work. An export is
/// accepted, committed and then published; if the broker is unreachable at that moment the
/// publish goes nowhere and the row is the only evidence anybody asked for a file. This notices
/// and publishes again, so a broker outage delays exports rather than losing them.
/// </para>
/// </remarks>
[ScheduledJob(
    Key = "export-maintenance",
    Group = "maintenance",
    // Every ten minutes. The expiry half would be happy running nightly; the requeue half is the
    // recovery path from a broker outage, and an hour of nothing happening is too long to wait.
    Cron = "0 0/10 * * * ?",
    Description = "Deletes expired export files and requeues exports that stalled.")]
public sealed class ExportMaintenanceJob : ScheduledJobBase
{
    private readonly IExportMaintenanceService _exports;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="exports">Export maintenance service.</param>
    /// <param name="logger">Logger.</param>
    public ExportMaintenanceJob(IExportMaintenanceService exports, ILogger<ExportMaintenanceJob> logger)
        : base(logger)
    {
        _exports = exports;
    }

    /// <inheritdoc />
    protected override async Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        // Expiry first. A job that has just been requeued should not be considered for deletion
        // in the same pass, and running them this way round means it cannot be.
        await _exports.ExpireAsync(cancellationToken);
        await _exports.RequeueStalledAsync(cancellationToken);
    }
}
