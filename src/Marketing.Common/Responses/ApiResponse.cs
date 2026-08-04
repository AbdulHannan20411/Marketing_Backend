namespace Marketing.Common.Responses;

/// <summary>
/// Envelope returned by every successful endpoint.
/// <para>
/// Failures are <em>not</em> wrapped in this type - they return RFC 7807 <c>ProblemDetails</c>,
/// so a client can branch on the content type rather than inspecting a success flag.
/// </para>
/// </summary>
/// <typeparam name="TData">Payload type.</typeparam>
public sealed class ApiResponse<TData>
{
    private ApiResponse(TData data, string? message, IReadOnlyDictionary<string, object?>? meta)
    {
        Data = data;
        Message = message;
        Meta = meta;
    }

    /// <summary>The payload.</summary>
    public TData Data { get; }

    /// <summary>Optional human-readable message suitable for a toast notification.</summary>
    public string? Message { get; }

    /// <summary>Optional out-of-band metadata such as paging information.</summary>
    public IReadOnlyDictionary<string, object?>? Meta { get; }

    /// <summary>Always <see langword="true"/>; present so clients can assert on a stable shape.</summary>
    public bool Success => true;

    /// <summary>Wraps a payload.</summary>
    public static ApiResponse<TData> Ok(TData data, string? message = null) => new(data, message, null);

    /// <summary>Wraps a payload together with metadata.</summary>
    public static ApiResponse<TData> Ok(
        TData data,
        IReadOnlyDictionary<string, object?> meta,
        string? message = null) => new(data, message, meta);
}

/// <summary>Non-generic helpers for building <see cref="ApiResponse{TData}"/> instances.</summary>
public static class ApiResponse
{
    /// <summary>Wraps a payload, inferring the type parameter.</summary>
    public static ApiResponse<TData> Ok<TData>(TData data, string? message = null) =>
        ApiResponse<TData>.Ok(data, message);

    /// <summary>
    /// Wraps a paged result and lifts its paging counters into <see cref="ApiResponse{TData}.Meta"/>
    /// so clients read them from one predictable place.
    /// </summary>
    /// <remarks>
    /// Named distinctly rather than overloading <see cref="Ok{TData}"/>. Both would be applicable
    /// for a <see cref="PagedResult{TItem}"/> argument, and relying on the compiler picking the
    /// more specific one is the kind of subtlety that silently changes a response shape when
    /// someone later adjusts a signature.
    /// </remarks>
    public static ApiResponse<IReadOnlyList<TItem>> OkPage<TItem>(PagedResult<TItem> page, string? message = null)
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pageNumber"] = page.PageNumber,
            ["pageSize"] = page.PageSize,
            ["totalCount"] = page.TotalCount,
            ["totalPages"] = page.TotalPages,
            ["hasPreviousPage"] = page.HasPreviousPage,
            ["hasNextPage"] = page.HasNextPage,
        };

        return ApiResponse<IReadOnlyList<TItem>>.Ok(page.Items, meta, message);
    }
}
