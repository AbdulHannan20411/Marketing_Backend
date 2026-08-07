using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Marketing.Infrastructure.WhatsApp;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

public sealed class MetaWebhookVerifierTests
{
    private const string AppSecret = "0123456789abcdef0123456789abcdef";
    private const string VerifyToken = "a-shared-verify-token";

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(
        """{"object":"whatsapp_business_account","entry":[]}""");

    private static MetaWebhookVerifier CreateVerifier() =>
        new(Options.Create(new WhatsAppOptions
        {
            AppId = "000000000000000",
            AppSecret = AppSecret,
            WebhookVerifyToken = VerifyToken,
        }));

    private static string Sign(byte[] payload) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), payload));

    [Fact]
    public void A_correctly_signed_payload_is_accepted()
    {
        CreateVerifier().IsSignatureValid(Payload, Sign(Payload)).Should().BeTrue();
    }

    [Fact]
    public void An_uppercase_signature_is_accepted()
    {
        // Meta sends lowercase hex, but hex is case-insensitive and rejecting the other casing
        // would be a self-inflicted outage the day that changes.
        var signature = Sign(Payload).ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal);

        CreateVerifier().IsSignatureValid(Payload, signature).Should().BeTrue();
    }

    [Fact]
    public void A_payload_altered_after_signing_is_refused()
    {
        var signature = Sign(Payload);
        var tampered = Encoding.UTF8.GetBytes(
            """{"object":"whatsapp_business_account","entry":[{"id":"1"}]}""");

        // This is the attack the signature exists to stop: a body that would otherwise be applied
        // to tenant data by an endpoint with no authentication of its own.
        CreateVerifier().IsSignatureValid(tampered, signature).Should().BeFalse();
    }

    [Fact]
    public void A_signature_made_with_the_wrong_secret_is_refused()
    {
        var forged = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("not-the-app-secret"), Payload));

        CreateVerifier().IsSignatureValid(Payload, forged).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]
    [InlineData("sha256=")]
    [InlineData("sha256=nothexnothexnothexnothexnothexnothexnothexnothexnothexnothexnoth")]
    [InlineData("sha1=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void A_missing_or_malformed_signature_is_refused(string? header)
    {
        CreateVerifier().IsSignatureValid(Payload, header).Should().BeFalse();
    }

    [Fact]
    public void The_subscription_handshake_accepts_the_configured_token()
    {
        CreateVerifier().IsValidSubscription("subscribe", VerifyToken).Should().BeTrue();
    }

    [Theory]
    [InlineData("subscribe", "wrong-token")]
    [InlineData("subscribe", null)]
    [InlineData("unsubscribe", VerifyToken)]
    [InlineData(null, VerifyToken)]
    public void The_subscription_handshake_refuses_anything_else(string? mode, string? token)
    {
        CreateVerifier().IsValidSubscription(mode, token).Should().BeFalse();
    }
}
