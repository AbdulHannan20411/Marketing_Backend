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
    public const int MaxPageSize = 100;

    /// <summary>Page size used when the caller does not specify one.</summary>
    public const int DefaultPageSize = 25;

    private int _pageNumber = 1;
    private int _pageSize = DefaultPageSize;

    /// <summary>
    /// One-based page number, bound from <c>?page=</c>. Values below one are coerced to one.
    /// </summary>
    /// <remarks>
    /// Named <c>Page</c> because query binding matches on the property name and <c>page</c> is what
    /// the client sends - the previous name, <c>PageNumber</c>, silently never bound and every
    /// request returned page one. <see cref="PageNumber"/> survives as an accepted alias; both
    /// write the same value. No binding attribute is used, because that would drag an MVC
    /// dependency into the innermost layer.
    /// </remarks>
    public int Page
    {
        get => _pageNumber;
        init => _pageNumber = value < 1 ? 1 : value;
    }

    /// <summary>Alias for <see cref="Page"/>, bound from <c>?pageNumber=</c>.</summary>
    public int PageNumber
    {
        get => _pageNumber;
        init
        {
            // Only an explicit value wins. Without this guard, a request carrying page=3 and no
            // pageNumber would have the alias bind its default of 0 and silently reset the page.
            if (value > 0)
            {
                _pageNumber = value;
            }
        }
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

    /// <summary>Rows to skip, derived from <see cref="Page"/> and <see cref="PageSize"/>.</summary>
    public int Skip => (_pageNumber - 1) * _pageSize;

    /// <summary>Rows to take.</summary>
    public int Take => _pageSize;

    /// <summary>Replaces the page size after binding, for endpoints with their own default.</summary>
    /// <param name="pageSize">Desired size, clamped like the bound value.</param>
    protected void SetDefaultPageSize(int pageSize) =>
        _pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;
}
