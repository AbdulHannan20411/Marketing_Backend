using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Common.Requests;

/// <summary>
/// Base query-string contract for every paged, filtered, sorted collection endpoint.
/// <para>
/// <see cref="PageSize"/> is clamped rather than validated so that a client asking for ten
/// thousand rows gets the maximum page instead of a 400 - and, more importantly, so no caller can
/// turn a list endpoint into an unbounded table scan.
/// </para>
/// </summary>
public class PageRequest
{
    /// <summary>Largest page a caller may request.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Page size used when the caller does not specify one.</summary>
    public const int DefaultPageSize = 25;

    private readonly int _pageNumber = 1;
    private readonly int _pageSize = DefaultPageSize;

    /// <summary>One-based page number. Values below one are coerced to one.</summary>
    public int PageNumber
    {
        get => _pageNumber;
        init => _pageNumber = value < 1 ? 1 : value;
    }

    /// <summary>Page size, clamped to <c>[1, <see cref="MaxPageSize"/>]</c>.</summary>
    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => value,
        };
    }

    /// <summary>Free-text search term applied to the endpoint's designated searchable columns.</summary>
    public string? Search { get; init; }

    /// <summary>
    /// Property to sort by. Endpoints validate this against an allow-list before it reaches the
    /// database; it is never interpolated into SQL.
    /// </summary>
    public string? SortBy { get; init; }

    /// <summary>Sort direction.</summary>
    public SortDirection SortDirection { get; init; } = SortDirection.Ascending;

    /// <summary>Rows to skip, derived from <see cref="PageNumber"/> and <see cref="PageSize"/>.</summary>
    public int Skip => (PageNumber - 1) * PageSize;

    /// <summary>Rows to take.</summary>
    public int Take => PageSize;
}
