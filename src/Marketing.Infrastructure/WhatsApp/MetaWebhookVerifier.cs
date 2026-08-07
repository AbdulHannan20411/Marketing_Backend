using System.Security.Cryptography;
using System.Text;
using Marketing.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.WhatsApp;

/// <summary>Verifies Meta webhook handshakes and payload signatures.</summary>
public sealed class MetaWebhookVerifier : IWhatsAppWebhookVerifier
{
    /// <summary>Value Meta sends in <c>hub.mode</c> when subscribing.</summary>
    private const string SubscribeMode = "subscribe";

    /// <summary>Prefix on the signature header, naming the digest used.</summary>
    private const string SignaturePrefix = "sha256=";

    /// <summary>Hex characters in a SHA-256 digest.</summary>
    private const int DigestHexLength = 64;

    private readonly WhatsAppOptions _options;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">WhatsApp settings, carrying the app secret and verify token.</param>
    public MetaWebhookVerifier(IOptions<WhatsAppOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    /// <inheritdoc />
    public bool IsValidSubscription(string? mode, string? verifyToken)
    {
        if (!string.Equals(mode, SubscribeMode, StringComparison.Ordinal) || verifyToken is null)
        {
            return false;
        }

        // Fixed-time comparison. The verify token is a shared secret, and an ordinary string
        // comparison leaks its prefix through timing to anyone willing to try often enough.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(verifyToken),
            Encoding.UTF8.GetBytes(_options.WebhookVerifyToken));
    }

    /// <inheritdoc />
    public bool IsSignatureValid(ReadOnlySpan<byte> payload, string? signatureHeader)
    {
        if (signatureHeader is null
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var provided = signatureHeader.AsSpan(SignaturePrefix.Length);

        if (provided.Length != DigestHexLength)
        {
            return false;
        }

        byte[] supplied;

        try
        {
            supplied = Convert.FromHexString(provided);
        }
        catch (FormatException)
        {
            // Not hex. Nothing to compare against, and no reason to spend a hash computation on it.
            return false;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];

        HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.AppSecret), payload, expected);

        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
