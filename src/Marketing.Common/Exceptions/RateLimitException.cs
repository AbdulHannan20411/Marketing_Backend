using System.Net;

namespace Marketing.Common.Exceptions;

/// <summary>
/// A caller has exhausted an allowance the platform sets for them.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ProviderQuotaException"/> on purpose. This one means "you have had your
/// share for now" and resolves by waiting; the other means the platform itself has run out and
/// waiting will not help. Telling a user to try later when nothing they do will fix it wastes their
/// afternoon, and the two look identical in metrics unless they are separated here.
/// </remarks>
public sealed class RateLimitException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="errorCode">Stable code the client branches on.</param>
    /// <param name="message">Message shown to the caller.</param>
    public RateLimitException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.TooManyRequests;

    /// <inheritdoc />
    public override string ErrorCode { get; }
}

/// <summary>
/// An upstream provider's own quota is exhausted, rather than the caller's.
/// </summary>
/// <remarks>
/// 402 rather than 429: this is a billing state on the platform's account, not something the user
/// did, and no amount of waiting on their part clears it. The wording differs too - one is "try
/// later", this one is "contact support".
/// <para>
/// <b>The provider is never named in the message.</b> This text reaches an end user, and which
/// supplier the platform buys from, and how much of it is left, is not their business.
/// </para>
/// </remarks>
public sealed class ProviderQuotaException : AppException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="errorCode">Stable code the client branches on.</param>
    /// <param name="message">Message shown to the caller. Must not name the provider.</param>
    public ProviderQuotaException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    /// <inheritdoc />
    public override HttpStatusCode StatusCode => HttpStatusCode.PaymentRequired;

    /// <inheritdoc />
    public override string ErrorCode { get; }
}
