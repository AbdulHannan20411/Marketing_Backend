using System.Text.Json.Serialization;

namespace Marketing.Common.Responses;

/// <summary>
/// One page of results, shaped exactly as the front-end contract specifies:
/// <c>{ items, page, pageSize, totalItems, totalPages }</c>.
/// </summary>
/// <typeparam name="TItem">Item type. Always a DTO, never a domain entity.</typeparam>
public sealed class PagedResult<TItem>
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="items">Items on the current page.</param>
    /// <param name="totalItems">Total matching items across all pages.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Requested page size.</param>
    public PagedResult(IReadOnlyList<TItem> items, int totalItems, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(totalItems);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        Items = items;
        TotalItems = totalItems;
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>Items on the current page.</summary>
    public IReadOnlyList<TItem> Items { get; }

    /// <summary>One-based page number.</summary>
    public int Page { get; }

    /// <summary>Requested page size.</summary>
    public int PageSize { get; }

    /// <summary>Total matching items across all pages.</summary>
    public int TotalItems { get; }

    /// <summary>
    /// Total number of pages, never below one.
    /// <para>
    /// The floor is a front-end requirement, not an arithmetic one: an empty result still renders
    /// as "Page 1 of 1" rather than "Page 1 of 0".
    /// </para>
    /// </summary>
    public int TotalPages =>
        TotalItems == 0 ? 1 : (int)Math.Ceiling(TotalItems / (double)PageSize);

    /// <summary>Whether a previous page exists. Server-side convenience; not serialised.</summary>
    [JsonIgnore]
    public bool HasPreviousPage => Page > 1;

    /// <summary>Whether a further page exists. Server-side convenience; not serialised.</summary>
    [JsonIgnore]
    public bool HasNextPage => TotalItems > 0 && Page < TotalPages;

    /// <summary>Projects the items onto a new type, preserving the paging counters.</summary>
    /// <typeparam name="TTarget">Projected item type.</typeparam>
    /// <param name="selector">Projection applied to each item.</param>
    public PagedResult<TTarget> Map<TTarget>(Func<TItem, TTarget> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return new PagedResult<TTarget>([.. Items.Select(selector)], TotalItems, Page, PageSize);
    }
}

/// <summary>Factory methods for <see cref="PagedResult{TItem}"/>.</summary>
public static class PagedResults
{
    /// <summary>Creates an empty page.</summary>
    /// <typeparam name="TItem">Item type.</typeparam>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Requested page size.</param>
    public static PagedResult<TItem> Empty<TItem>(int page, int pageSize) =>
        new([], totalItems: 0, page, pageSize);
}
