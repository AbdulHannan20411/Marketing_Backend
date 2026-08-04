using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when an authenticated principal lacks the role, policy or permission required for an
/// operation it is otherwise allowed to know exists.
/// </summary>
public sealed class ForbiddenException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">Reason access was denied.</param>
    public ForbiddenException(string message = "You do not have permission to perform this action.")
        : base(message)
    {
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.Forbidden;

    /// <inheritdoc />
    public override string ErrorCode => "forbidden";
}
