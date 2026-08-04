using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Base type for every exception the application raises deliberately.
/// <para>
/// Carrying the HTTP status and an RFC 7807 problem type on the exception lets the global handler
/// translate any of them without a type switch, and guarantees that an exception which is not an
/// <see cref="AppException"/> is by definition unexpected and therefore logged at error level.
/// </para>
/// </summary>
public abstract class AppException : Exception
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">Operator-facing message. Must never contain secrets or PII.</param>
    /// <param name="innerException">Underlying cause, if any.</param>
    protected AppException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status the global exception handler should return.</summary>
    public abstract HttpStatusCode StatusCode { get; }

    /// <summary>Stable, machine-readable error code returned to the client.</summary>
    public abstract string ErrorCode { get; }

    /// <summary>
    /// Whether the message is safe to return verbatim to the caller.
    /// Server-side failures return a generic message plus an exception id instead.
    /// </summary>
    public virtual bool IsClientSafe => true;

    /// <summary>Additional key/value pairs merged into the <c>ProblemDetails.Extensions</c> bag.</summary>
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
