using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when a request is well-formed and authorised but violates a domain rule - for example
/// scheduling a campaign against a WhatsApp number that is not yet verified.
/// </summary>
public sealed class BusinessRuleException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="errorCode">Stable snake_case code the client can branch on.</param>
    /// <param name="message">Human-readable explanation, safe to display.</param>
    public BusinessRuleException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.Conflict;

    /// <inheritdoc />
    public override string ErrorCode { get; }
}
