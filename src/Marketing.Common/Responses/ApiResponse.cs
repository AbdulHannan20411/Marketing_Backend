namespace Marketing.Common.Responses;

/// <summary>
/// Envelope returned by every successful endpoint.
/// <para>
/// Failures are <em>not</em> wrapped in this type—they return RFC 7807
/// <c>ProblemDetails</c>, allowing clients to branch on the response
/// content type instead of inspecting a success flag.
/// </para>
/// </summary>
/// <typeparam name="TData">Payload type.</typeparam>
public sealed class ApiResponse<TData>
{
    internal ApiResponse(
        TData data,
        string? message,
        IReadOnlyDictionary<string, object?>? meta)
    {
        Data = data;
        Message = message;
        Meta = meta;
    }

    /// <summary>
    /// Gets the response payload.
    /// </summary>
    public TData Data { get; }

    /// <summary>
    /// Gets an optional human-readable message suitable for display to users.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets optional out-of-band metadata such as paging information.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Meta { get; }

    /// <summary>
    /// Always <see langword="true"/> for successful responses.
    /// </summary>
    public bool Success => true;
}

/// <summary>
/// Factory methods for creating <see cref="ApiResponse{TData}"/> instances.
/// </summary>
public static class ApiResponse
{
    /// <summary>
    /// Wraps a payload in a successful response.
    /// </summary>
    public static ApiResponse<TData> Ok<TData>(
        TData data,
        string? message = null)
    {
        return new ApiResponse<TData>(
            data,
            message,
            meta: null);
    }

    /// <summary>
    /// Wraps a payload together with metadata.
    /// </summary>
    public static ApiResponse<TData> Ok<TData>(
        TData data,
        IReadOnlyDictionary<string, object?> meta,
        string? message = null)
    {
        return new ApiResponse<TData>(
            data,
            message,
            meta);
    }

    /// <summary>
    /// Wraps a paged result and exposes paging information through
    /// <see cref="ApiResponse{TData}.Meta"/>.
    /// </summary>
    public static ApiResponse<IReadOnlyList<TItem>> OkPage<TItem>(
        PagedResult<TItem> page,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var meta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pageNumber"] = page.PageNumber,
            ["pageSize"] = page.PageSize,
            ["totalCount"] = page.TotalCount,
            ["totalPages"] = page.TotalPages,
            ["hasPreviousPage"] = page.HasPreviousPage,
            ["hasNextPage"] = page.HasNextPage,
        };

        return new ApiResponse<IReadOnlyList<TItem>>(
            page.Items,
            message,
            meta);
    }
}
