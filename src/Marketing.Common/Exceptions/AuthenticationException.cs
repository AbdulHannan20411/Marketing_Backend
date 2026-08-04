using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// Raised when credentials or a token are missing, malformed, expired or revoked.
/// <para>
/// The message is intentionally uniform across every failure mode - wrong password, unknown
/// address, disabled account - so the response cannot be used to enumerate valid users.
/// </para>
/// </summary>
public sealed class AuthenticationException : AppException
{
    private const string GenericMessage = "The credentials provided are invalid.";

    /// <summary>Initialises a new instance carrying the generic message.</summary>
    /// <param name="errorCode">Stable code; safe to vary even when the message does not.</param>
    public AuthenticationException(string errorCode = "invalid_credentials")
        : base(GenericMessage)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Initialises a new instance with an explicit message. Use only when disclosure is safe.</summary>
    /// <param name="errorCode">Stable code the client can branch on.</param>
    /// <param name="message">Message to return; must not reveal whether an account exists.</param>
    public AuthenticationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.Unauthorized;

    /// <inheritdoc />
    public override string ErrorCode { get; }
}
