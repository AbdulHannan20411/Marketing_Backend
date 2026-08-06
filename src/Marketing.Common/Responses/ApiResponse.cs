namespace Marketing.Common.Responses;

/// <summary>
/// Envelope returned by every successful endpoint.
/// <para>
/// The shape is fixed by the front-end contract: <c>ApiService</c> unwraps <c>data</c> centrally,
/// so an unwrapped body breaks every screen. Failures are deliberately <em>not</em> wrapped - they
/// return RFC 7807 problem documents, letting the client branch on content type rather than
/// inspecting a success flag.
/// </para>
/// </summary>
/// <typeparam name="TData">Payload type.</typeparam>
public sealed class ApiResponse<TData>
{
    internal ApiResponse(TData data, string? message, string traceId)
    {
        Data = data;
        Message = message;
        TraceId = traceId;
    }

    /// <summary>The payload, or <see langword="null"/> for endpoints that return nothing.</summary>
    public TData Data { get; }

    /// <summary>
    /// Optional message surfaced to the user as a success toast.
    /// <para>
    /// Set it to confirm a write ("Plan \"Growth\" saved."); leave it null on reads, or the user
    /// gets a toast every time a list refreshes.
    /// </para>
    /// </summary>
    public string? Message { get; }

    /// <summary>Correlation id, echoed in the logs so a support report can be traced to a request.</summary>
    public string TraceId { get; }
}

/// <summary>Factory methods for <see cref="ApiResponse{TData}"/>.</summary>
public static class ApiResponse
{
    /// <summary>Wraps a payload.</summary>
    /// <typeparam name="TData">Payload type.</typeparam>
    /// <param name="data">Payload.</param>
    /// <param name="traceId">Correlation id for this request.</param>
    /// <param name="message">Optional success message.</param>
    public static ApiResponse<TData> Ok<TData>(TData data, string traceId, string? message = null) =>
        new(data, message, traceId);

    /// <summary>
    /// Wraps an empty payload, for endpoints the contract defines as returning <c>null</c> data.
    /// </summary>
    /// <param name="traceId">Correlation id for this request.</param>
    /// <param name="message">Optional success message.</param>
    public static ApiResponse<object?> Empty(string traceId, string? message = null) =>
        new(null, message, traceId);
}
