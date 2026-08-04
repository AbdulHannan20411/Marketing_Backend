namespace Marketing.Common.Responses;

/// <summary>One page of results plus the counters a client needs to render pagination.</summary>
/// <typeparam name="TItem">Item type; always a DTO, never an entity.</typeparam>
public sealed class PagedResult<TItem>
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="items">Items on this page.</param>
    /// <param name="totalCount">Total matching rows across all pages.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Requested page size.</param>
    public PagedResult(IReadOnlyList<TItem> items, int totalCount, int pageNumber, int pageSize)
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

    /// <summary>Items on this page.</summary>
    public IReadOnlyList<TItem> Items { get; }

    /// <summary>Total matching rows across all pages.</summary>
    public int TotalCount { get; }

    /// <summary>One-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>Page size that produced this result.</summary>
    public int PageSize { get; }

    /// <summary>Total number of pages, at least one even when empty.</summary>
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>Whether a previous page exists.</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>Whether a further page exists.</summary>
    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>An empty page, used to short-circuit queries that cannot match anything.</summary>
    public static PagedResult<TItem> Empty(int pageNumber, int pageSize) =>
        new([], 0, pageNumber, pageSize);

    /// <summary>Projects the items of this page onto a new type, preserving the counters.</summary>
    /// <typeparam name="TTarget">Projected item type.</typeparam>
    /// <param name="selector">Projection applied to each item.</param>
    public PagedResult<TTarget> Map<TTarget>(Func<TItem, TTarget> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new PagedResult<TTarget>([.. Items.Select(selector)], TotalCount, PageNumber, PageSize);
    }
}
