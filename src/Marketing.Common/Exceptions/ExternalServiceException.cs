using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when a downstream dependency - most often the Meta WhatsApp Cloud API - fails, times
/// out, or trips a circuit breaker.
/// <para>
/// The upstream message is never client-safe: it can contain access tokens, internal identifiers
/// and Meta trace ids. The handler returns a generic 502 plus an exception id instead.
/// </para>
/// </summary>
public sealed class ExternalServiceException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="serviceName">Name of the dependency, for example <c>MetaCloudApi</c>.</param>
    /// <param name="message">Diagnostic message, logged but not returned.</param>
    /// <param name="innerException">Underlying transport or deserialisation failure.</param>
    /// <param name="isTransient">Whether a retry could plausibly succeed.</param>
    public ExternalServiceException(
        string serviceName,
        string message,
        Exception? innerException = null,
        bool isTransient = false)
        : base(message, innerException)
    {
        ServiceName = serviceName;
        IsTransient = isTransient;
        Extensions["service"] = serviceName;
    }

    /// <summary>Dependency that failed.</summary>
    public string ServiceName { get; }

    /// <summary>Whether the failure is transient and safe to retry.</summary>
    public bool IsTransient { get; }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode =>
        IsTransient ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadGateway;

    /// <inheritdoc />
    public override string ErrorCode => IsTransient ? "external_service_unavailable" : "external_service_error";

    /// <inheritdoc />
    public override bool IsClientSafe => false;
}
