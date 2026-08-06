using System.Linq.Expressions;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Persistence operations shared by every aggregate.
/// <para>
/// Strictly data access: no validation, no authorisation, no orchestration. Those belong in
/// <c>Marketing.Application</c> services. Keeping the split honest is what allows a service to be
/// unit-tested against a substituted repository without a database.
/// </para>
/// </summary>
/// <typeparam name="TEntity">Aggregate root.</typeparam>
public interface IRepository<TEntity>
    where TEntity : BaseEntity
{
    /// <summary>
    /// Composable query over live, in-tenant rows.
    /// <para>
    /// No-tracking by default: the overwhelming majority of queries are reads, and tracking them
    /// costs a snapshot per row for nothing.
    /// </para>
    /// </summary>
    /// <param name="asNoTracking">Whether to disable change tracking.</param>
    public IQueryable<TEntity> Query(bool asNoTracking = true);

    /// <summary>Fetches a row by key without tracking. Returns null when absent or out of tenant.</summary>
    public Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Fetches a tracked row by key, ready to be mutated and saved.</summary>
    public Task<TEntity?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Fetches the first row matching a predicate, without tracking.</summary>
    public Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether any row matches a predicate.</summary>
    public Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>Counts rows matching an optional predicate.</summary>
    public Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of projected rows.
    /// <para>
    /// The projection is applied in the database, so only the selected columns cross the wire and
    /// an entity can never escape this layer.
    /// </para>
    /// </summary>
    /// <typeparam name="TProjection">Projected shape, always a DTO.</typeparam>
    /// <param name="request">Paging, sorting and search parameters.</param>
    /// <param name="projection">Projection evaluated server-side.</param>
    /// <param name="allowedSorts">Allow-list of sortable fields.</param>
    /// <param name="defaultSort">Sort applied when the request specifies none.</param>
    /// <param name="filter">Optional additional predicate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<TProjection>> GetPagedAsync<TProjection>(
        PageRequest request,
        Expression<Func<TEntity, TProjection>> projection,
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> allowedSorts,
        Expression<Func<TEntity, object?>> defaultSort,
        Expression<Func<TEntity, bool>>? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stages an insert.</summary>
    public void Add(TEntity entity);

    /// <summary>Stages several inserts.</summary>
    public void AddRange(IEnumerable<TEntity> entities);

    /// <summary>Stages an update for a detached instance.</summary>
    public void Update(TEntity entity);

    /// <summary>Stages a soft delete. The interceptor rewrites it into an <c>IsDeleted</c> update.</summary>
    public void Remove(TEntity entity);

    /// <summary>Stages several soft deletes.</summary>
    public void RemoveRange(IEnumerable<TEntity> entities);
}
