using AwesomeAssertions;
using Marketing.Infrastructure.Security;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Round-trips a Meta access token through the protector at its real length.
/// </summary>
/// <remarks>
/// Written while diagnosing a connect failure where Meta accepted the raw token but rejected the
/// one the application sent. A protector that truncates, or a column too narrow for the ciphertext,
/// would produce exactly that: a credential that looks stored and is silently unusable.
/// </remarks>
public sealed class SecretProtectorRoundTripTests
{
    /// <summary>Length of the column the ciphertext has to fit in.</summary>
    private const int ColumnLength = 2048;

    /// <summary>A Meta test-number token is around 290 characters; a system-user token is longer.</summary>
    private const int RealisticTokenLength = 290;

    private static AesGcmSecretProtector CreateProtector()
    {
        var options = new SecurityOptions
        {
            ActiveKeyId = "k1",
            EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["k1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            },
        };

        return new AesGcmSecretProtector(Options.Create(options));
    }

    [Theory]
    [InlineData(RealisticTokenLength)]
    [InlineData(512)]
    [InlineData(1000)]
    public void A_token_survives_the_round_trip_unchanged(int length)
    {
        var token = "EAA" + new string('x', length - 3);
        var protector = CreateProtector();

        var restored = protector.Unprotect(protector.Protect(token));

        // Not "starts with" or "contains": a credential that differs by one character is a
        // credential that does not work, and the failure surfaces as an opaque provider error.
        restored.Should().Be(token);
    }

    [Fact]
    public void The_ciphertext_fits_the_column_it_is_stored_in()
    {
        // A ciphertext longer than the column is truncated on write and unreadable on read, which
        // reads as "the provider rejected our token" rather than as data loss.
        var token = "EAA" + new string('x', RealisticTokenLength - 3);

        CreateProtector().Protect(token).Length.Should().BeLessThan(ColumnLength);
    }
}
