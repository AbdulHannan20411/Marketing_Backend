using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when an authenticated principal lacks the role, policy or permission required for an
/// operation it is otherwise allowed to know exists.
/// </summary>
public sealed class ForbiddenException : AppException
{
    private readonly string _errorCode;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">Reason access was denied.</param>
    public ForbiddenException(string message = "You do not have permission to perform this action.")
        : this("forbidden", message)
    {
    }

    /// <summary>Initialises a new instance with a more specific code than <c>forbidden</c>.</summary>
    /// <remarks>
    /// For a refusal the client handles differently from a missing permission - access to one
    /// WhatsApp number rather than to the feature, where the remedy is a different person to ask.
    /// </remarks>
    /// <param name="errorCode">Stable machine-readable code.</param>
    /// <param name="message">Reason access was denied, as a sentence the user can act on.</param>
    public ForbiddenException(string errorCode, string message)
        : base(message)
    {
        _errorCode = errorCode;
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.Forbidden;

    /// <inheritdoc />
    public override string ErrorCode => _errorCode;
}
