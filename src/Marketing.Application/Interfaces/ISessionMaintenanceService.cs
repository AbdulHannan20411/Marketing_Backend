namespace Marketing.Application.Interfaces;

/// <summary>
/// Housekeeping over authentication sessions.
/// <para>
/// Lives in the business layer rather than inside the job so the operation can also be triggered
/// from an admin endpoint and unit-tested without a scheduler. The job is a trigger; this is the
/// work.
/// </para>
/// </summary>
public interface ISessionMaintenanceService
{
    /// <summary>
    /// Permanently removes refresh tokens that expired before the retention cut-off.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of tokens removed.</returns>
    public Task<int> PurgeExpiredSessionsAsync(CancellationToken cancellationToken = default);
}
