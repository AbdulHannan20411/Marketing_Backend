namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Commits the work staged across repositories.
/// <para>
/// Repositories deliberately do not save. A service that touches three aggregates commits once, so
/// a partial write is impossible without an explicit decision to make it possible.
/// </para>
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Commits all staged changes.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows affected.</returns>
    /// <exception cref="Common.Exceptions.ConcurrencyConflictException">A row changed underneath us.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside an explicit transaction, committing on success and
    /// rolling back on any exception.
    /// </summary>
    /// <typeparam name="TResult">Result produced by the operation.</typeparam>
    /// <param name="operation">Work to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Wrapped in the provider's execution strategy, which is mandatory once retries are enabled:
    /// a retried transaction must be replayed from its beginning, not resumed midway.
    /// </remarks>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>Runs an operation with no result inside an explicit transaction.</summary>
    /// <param name="operation">Work to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}
