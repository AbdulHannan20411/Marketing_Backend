namespace Marketing.Common.Requests;

/// <summary>
/// Paging a caller may ask for, or leave out entirely.
/// </summary>
/// <remarks>
/// <see cref="PageRequest"/> coerces its values as they bind, which makes an absent <c>page</c>
/// indistinguishable from <c>page=1</c>. That is right for a route that has always been paged and
/// wrong for one that is gaining paging: these lists must keep answering with a plain array for
/// the clients that already read them that way, and only a nullable value can tell the two apart.
/// </remarks>
public class OptionalPageRequest
{
    /// <summary>Page size used when the caller asks for a page without saying how big.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Largest page a caller may ask for.</summary>
    public const int MaxPageSize = PageRequest.MaxPageSize;

    /// <summary>One-based page number, or null when the caller did not ask for a page.</summary>
    public int? Page { get; init; }

    /// <summary>Rows per page, or null for the endpoint's default.</summary>
    public int? PageSize { get; init; }

    /// <summary>Free-text search applied to the endpoint's searchable columns.</summary>
    public string? Search { get; init; }

    /// <summary>Whether the caller asked for a page rather than the whole list.</summary>
    public bool WantsPage => Page.HasValue || PageSize.HasValue;

    /// <summary>The page to return, never below one.</summary>
    public int Number => Page is { } page && page > 1 ? page : 1;

    /// <summary>The page size to use, clamped to <c>[1, <see cref="MaxPageSize"/>]</c>.</summary>
    /// <param name="fallback">Size to use when the caller did not ask for one.</param>
    public int Size(int fallback = DefaultPageSize) =>
        Math.Clamp(PageSize ?? fallback, 1, MaxPageSize);
}
