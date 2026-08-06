using Marketing.Common.Responses;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Materialises a composed <see cref="IQueryable{T}"/>.
/// <para>
/// Exists so that services can compose and project queries - which is business logic about what a
/// screen needs - without the Application layer referencing Entity Framework. Composition happens
/// upstairs; the asynchronous execution that needs the provider happens here.
/// </para>
/// </summary>
public interface IQueryExecutor
{
    /// <summary>Materialises every row.</summary>
    public Task<IReadOnlyList<TResult>> ToListAsync<TResult>(
        IQueryable<TResult> query,
        CancellationToken cancellationToken = default);

    /// <summary>Materialises the first row, or null.</summary>
    public Task<TResult?> FirstOrDefaultAsync<TResult>(
        IQueryable<TResult> query,
        CancellationToken cancellationToken = default);

    /// <summary>Counts matching rows.</summary>
    public Task<int> CountAsync<TResult>(
        IQueryable<TResult> query,
        CancellationToken cancellationToken = default);

    /// <summary>Sums an integer projection, returning zero for an empty set.</summary>
    public Task<int> SumAsync(
        IQueryable<int> query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materialises one page and its total.
    /// <para>
    /// Two queries rather than a windowed count: on PostgreSQL a <c>count(*) over ()</c> forces
    /// the planner to materialise the whole result set before applying the limit.
    /// </para>
    /// </summary>
    /// <param name="query">Ordered, projected query.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<TResult>> ToPagedAsync<TResult>(
        IQueryable<TResult> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
