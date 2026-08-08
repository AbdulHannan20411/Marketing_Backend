using System.Linq.Expressions;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Entity Framework implementation of <see cref="IRepository{TEntity}"/>.</summary>
/// <typeparam name="TEntity">Aggregate root.</typeparam>
public class Repository<TEntity> : IRepository<TEntity>
    where TEntity : BaseEntity
{
    /// <summary>Database context. Available to derived repositories for aggregate-specific queries.</summary>
    protected ApplicationDbContext Context { get; }

    /// <summary>Entity set for <typeparamref name="TEntity"/>.</summary>
    protected DbSet<TEntity> Set { get; }

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public Repository(ApplicationDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Context = context;
        Set = context.Set<TEntity>();
    }

    /// <inheritdoc />
    public IQueryable<TEntity> Query(bool asNoTracking = true) =>
        asNoTracking ? Set.AsNoTracking() : Set.AsQueryable();

    /// <inheritdoc />
    public Task<TEntity?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<TEntity?> GetForUpdateAsync(long id, CancellationToken cancellationToken = default) =>
        Set.FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(predicate, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        Set.AsNoTracking().AnyAsync(predicate, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default) =>
        predicate is null
            ? Set.AsNoTracking().CountAsync(cancellationToken)
            : Set.AsNoTracking().CountAsync(predicate, cancellationToken);

    /// <inheritdoc />
    public async Task<PagedResult<TProjection>> GetPagedAsync<TProjection>(
        PageRequest request,
        Expression<Func<TEntity, TProjection>> projection,
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> allowedSorts,
        Expression<Func<TEntity, object?>> defaultSort,
        Expression<Func<TEntity, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(projection);

        var query = Set.AsNoTracking()
            .WhereIf(filter is not null, filter!)
            .ApplySort(request, allowedSorts, defaultSort);

        var totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResults.Empty<TProjection>(request.PageNumber, request.PageSize);
        }

        // Project before paging so the SELECT list is narrow and the projection participates in
        // the same plan as the sort and limit.
        var items = await query
            .Skip(request.Skip)
            .Take(request.Take)
            .Select(projection)
            .ToListAsync(cancellationToken);

        return new PagedResult<TProjection>(items, totalCount, request.PageNumber, request.PageSize);
    }

    /// <inheritdoc />
    public void Add(TEntity entity) => Set.Add(entity);

    /// <inheritdoc />
    public void AddRange(IEnumerable<TEntity> entities) => Set.AddRange(entities);

    /// <inheritdoc />
    public void Update(TEntity entity) => Set.Update(entity);

    /// <inheritdoc />
    public void Remove(TEntity entity) => Set.Remove(entity);

    /// <inheritdoc />
    public void RemoveRange(IEnumerable<TEntity> entities) => Set.RemoveRange(entities);
}
