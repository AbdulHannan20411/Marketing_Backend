using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when an optimistic concurrency check fails: the row was modified by someone else
/// between the read and the write.
/// </summary>
public sealed class ConcurrencyConflictException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="resourceName">Logical resource that conflicted.</param>
    /// <param name="innerException">The provider-level concurrency exception.</param>
    public ConcurrencyConflictException(string resourceName, Exception? innerException = null)
        : base($"{resourceName} was modified by another user. Reload it and apply your changes again.",
               innerException)
    {
        Extensions["resource"] = resourceName;
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.Conflict;

    /// <inheritdoc />
    public override string ErrorCode => "concurrency_conflict";
}
