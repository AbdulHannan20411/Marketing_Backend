using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// A request refused with a specific status and code that none of the general exceptions carry -
/// a file too large (413), of a type nobody accepts (415), or rejected by a dependency in a way the
/// user can act on.
/// </summary>
public sealed class RequestRejectedException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="statusCode">Status to answer with.</param>
    /// <param name="errorCode">Stable machine-readable code.</param>
    /// <param name="message">A sentence the user can act on.</param>
    /// <param name="field">The request field it concerns, when there is one; returned as <c>field</c>.</param>
    public RequestRejectedException(HttpStatusCode statusCode, string errorCode, string message, string? field = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;

        if (field is not null)
        {
            Extensions["field"] = field;
        }
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode { get; }

    /// <inheritdoc />
    public override string ErrorCode { get; }
}
