using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// The claim side of the import outbox.
/// <para>
/// Its own repository because the poll runs with no principal — the tenant filter would match
/// nothing — and because claiming has to be atomic across however many workers are running.
/// </para>
/// </summary>
public interface IImportJobRepository : IRepository<ImportJob>
{
    /// <summary>
    /// Claims the next batch of due jobs for this worker, across every tenant.
    /// </summary>
    /// <remarks>
    /// Claiming and reading are one statement, so two workers cannot take the same job. Anything
    /// claimed longer ago than the lease is treated as abandoned and reclaimed, which is what stops
    /// a worker that died mid-job from stalling an import for ever.
    /// </remarks>
    /// <param name="utcNow">Current instant.</param>
    /// <param name="lease">How long a claim is honoured before the job is considered abandoned.</param>
    /// <param name="maximum">Most jobs to claim in one poll.</param>
    /// <param name="maximumAttempts">Attempts after which a job is dead-lettered instead of retried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<ImportJob>> ClaimDueAsync(
        DateTimeOffset utcNow,
        TimeSpan lease,
        int maximum,
        int maximumAttempts,
        CancellationToken cancellationToken = default);
}
