namespace Marketing.Common.Constants;

/// <summary>Custom HTTP headers emitted or consumed by the API.</summary>
public static class ApplicationHeaderNames
{
    /// <summary>Correlation identifier that ties every log entry for one request together.</summary>
    public const string CorrelationId = "X-Correlation-Id";

    /// <summary>Identifier of a single request, regenerated even when a correlation id is reused.</summary>
    public const string RequestId = "X-Request-Id";

    /// <summary>Opaque identifier attached to an error response so support can locate the log entry.</summary>
    public const string ExceptionId = "X-Exception-Id";

    /// <summary>Total row count returned alongside a paged collection.</summary>
    public const string TotalCount = "X-Total-Count";

    /// <summary>Signature header Meta uses to sign webhook payloads.</summary>
    public const string MetaSignature = "X-Hub-Signature-256";
}
