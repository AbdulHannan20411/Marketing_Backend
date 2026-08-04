using System.Linq.Expressions;
using Marketing.Common.Enums;
using Marketing.Common.Exceptions;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Extensions;

/// <summary>Composable query helpers shared by every repository.</summary>
public static class QueryableExtensions
{
    /// <summary>Applies a predicate only when <paramref name="condition"/> holds.</summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <param name="source">Query being composed.</param>
    /// <param name="condition">Whether to apply the predicate.</param>
    /// <param name="predicate">Predicate to apply.</param>
    public static IQueryable<TSource> WhereIf<TSource>(
        this IQueryable<TSource> source,
        bool condition,
        Expression<Func<TSource, bool>> predicate) =>
        condition ? source.Where(predicate) : source;

    /// <summary>
    /// Applies a sort chosen from an allow-list.
    /// <para>
    /// The caller supplies a map from the API's sort keys to typed expressions, so a client-
    /// supplied sort field is matched against known keys and never reaches the provider as text.
    /// That closes the injection vector that dynamic-LINQ style sorting opens, and it also means an
    /// unsortable column cannot be used to force a sequential scan.
    /// </para>
    /// </summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <param name="source">Query being composed.</param>
    /// <param name="request">Paging request carrying the requested sort.</param>
    /// <param name="allowedSorts">Sort key to key-selector map. Comparison is case-insensitive.</param>
    /// <param name="defaultSort">Applied when the request specifies no sort.</param>
    /// <exception cref="ValidationException">The requested sort key is not in the allow-list.</exception>
    public static IQueryable<TSource> ApplySort<TSource>(
        this IQueryable<TSource> source,
        PageRequest request,
        IReadOnlyDictionary<string, Expression<Func<TSource, object?>>> allowedSorts,
        Expression<Func<TSource, object?>> defaultSort)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedSorts);

        if (string.IsNullOrWhiteSpace(request.SortBy))
        {
            return source.OrderByDescending(defaultSort);
        }

        if (!allowedSorts.TryGetValue(request.SortBy.Trim(), out var selector))
        {
            throw new ValidationException(
                nameof(PageRequest.SortBy),
                $"'{request.SortBy}' is not a sortable field. Allowed values: {string.Join(", ", allowedSorts.Keys)}.");
        }

        return request.SortDirection == SortDirection.Descending
            ? source.OrderByDescending(selector)
            : source.OrderBy(selector);
    }

    /// <summary>
    /// Materialises one page together with its total count.
    /// <para>
    /// Runs two queries rather than a windowed count, because on PostgreSQL a
    /// <c>count(*) over ()</c> forces the planner to materialise the full result set before
    /// applying the limit. Two round trips against the same index beat one that scans everything.
    /// </para>
    /// </summary>
    /// <typeparam name="TSource">Element type.</typeparam>
    /// <param name="source">Ordered query to page.</param>
    /// <param name="request">Paging request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<PagedResult<TSource>> ToPagedResultAsync<TSource>(
        this IQueryable<TSource> source,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var totalCount = await source.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult<TSource>.Empty(request.PageNumber, request.PageSize);
        }

        var items = await source
            .Skip(request.Skip)
            .Take(request.Take)
            .ToListAsync(cancellationToken);

        return new PagedResult<TSource>(items, totalCount, request.PageNumber, request.PageSize);
    }
}
