using System.Runtime.CompilerServices;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Responses;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Entity Framework implementation of <see cref="IQueryExecutor"/>.</summary>
public sealed class QueryExecutor : IQueryExecutor
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TResult>> ToListAsync<TResult>(
        IQueryable<TResult> query,
        CancellationToken cancellationToken = default) =>
        await query.ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<TResult?> FirstOrDefaultAsync<TResult>(
        IQueryable<TResult> query,
        CancellationToken cancellationToken = default) =>
        query.FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync<TResult>(
        IQueryable<TResult> query,
        CancellationToken cancellationToken = default) =>
        query.CountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> SumAsync(IQueryable<int> query, CancellationToken cancellationToken = default) =>
        await query.AnyAsync(cancellationToken) ? await query.SumAsync(cancellationToken) : 0;

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> ToPagedAsync<TResult>(
        IQueryable<TResult> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var totalItems = await query.CountAsync(cancellationToken);

        if (totalItems == 0)
        {
            return PagedResults.Empty<TResult>(page, pageSize);
        }

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<TResult>(items, totalItems, page, pageSize);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TResult> StreamAsync<TResult>(
        IQueryable<TResult> query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return row;
        }
    }
}
