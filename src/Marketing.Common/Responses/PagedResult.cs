namespace Marketing.Common.Responses;

/// <summary>
/// Represents one page of results together with the metadata required
/// to render pagination.
/// </summary>
/// <typeparam name="TItem">
/// The item type. This should be a DTO rather than a domain entity.
/// </typeparam>
public sealed class PagedResult<TItem>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PagedResult{TItem}"/> class.
    /// </summary>
    /// <param name="items">Items on the current page.</param>
    /// <param name="totalCount">Total number of matching items across all pages.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Number of items requested per page.</param>
    public PagedResult(
        IReadOnlyList<TItem> items,
        int totalCount,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        Items = items;
        TotalCount = totalCount;
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    /// <summary>
    /// Gets the items contained in the current page.
    /// </summary>
    public IReadOnlyList<TItem> Items { get; }

    /// <summary>
    /// Gets the total number of matching items across all pages.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Gets the current one-based page number.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Gets the requested page size.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the total number of pages.
    /// Returns <c>0</c> when there are no matching items.
    /// </summary>
    public int TotalPages =>
        TotalCount == 0
            ? 0
            : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>
    /// Gets a value indicating whether a previous page exists.
    /// </summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>
    /// Gets a value indicating whether a subsequent page exists.
    /// </summary>
    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>
    /// Projects the items in this page to another type while preserving
    /// the paging metadata.
    /// </summary>
    /// <typeparam name="TTarget">The projected item type.</typeparam>
    /// <param name="selector">Projection applied to each item.</param>
    /// <returns>A new paged result containing the projected items.</returns>
    public PagedResult<TTarget> Map<TTarget>(Func<TItem, TTarget> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return new PagedResult<TTarget>(
            [.. Items.Select(selector)],
            TotalCount,
            PageNumber,
            PageSize);
    }
}

/// <summary>
/// Factory methods for creating <see cref="PagedResult{TItem}"/> instances.
/// </summary>
public static class PagedResults
{
    /// <summary>
    /// Creates an empty page.
    /// </summary>
    public static PagedResult<TItem> Empty<TItem>(
        int pageNumber,
        int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return new PagedResult<TItem>(
            [],
            totalCount: 0,
            pageNumber,
            pageSize);
    }
}
