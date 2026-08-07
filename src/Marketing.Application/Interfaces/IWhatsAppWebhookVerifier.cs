namespace Marketing.Application.Interfaces;

/// <summary>
/// Decides whether an inbound Meta webhook is genuine.
/// <para>
/// A seam for the same reason the gateway is one: both checks are keyed on the Meta app secret,
/// which belongs in the infrastructure layer and must not be readable from a service.
/// </para>
/// <para>
/// The webhook endpoint is anonymous - Meta has no bearer token to present - so this is the
/// <em>only</em> thing standing between the open internet and code that writes to tenant data.
/// </para>
/// </summary>
public interface IWhatsAppWebhookVerifier
{
    /// <summary>
    /// Whether a subscription handshake should be answered with the challenge.
    /// </summary>
    /// <param name="mode">The <c>hub.mode</c> query value; Meta sends <c>subscribe</c>.</param>
    /// <param name="verifyToken">The <c>hub.verify_token</c> query value.</param>
    public bool IsValidSubscription(string? mode, string? verifyToken);

    /// <summary>
    /// Whether the payload's signature matches the app secret.
    /// </summary>
    /// <remarks>
    /// Computed over the exact bytes received. Re-serialising a parsed object and signing that
    /// would produce a different digest for a payload that is byte-for-byte legitimate.
    /// </remarks>
    /// <param name="payload">Raw request body.</param>
    /// <param name="signatureHeader">The <c>X-Hub-Signature-256</c> header value.</param>
    public bool IsSignatureValid(ReadOnlySpan<byte> payload, string? signatureHeader);
}
