using System.Security.Cryptography;
using AwesomeAssertions;
using Marketing.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

public sealed class AesGcmSecretProtectorTests
{
    private const string Token = "EAAG9ZBxyz0BOxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmSecretProtector CreateProtector(
        string activeKeyId = "k1",
        Dictionary<string, string>? keys = null) =>
        new(Options.Create(new SecurityOptions
        {
            EncryptionKeys = keys ?? new Dictionary<string, string> { ["k1"] = NewKey() },
            ActiveKeyId = activeKeyId,
        }));

    [Fact]
    public void A_protected_secret_round_trips()
    {
        var protector = CreateProtector();

        protector.Unprotect(protector.Protect(Token)).Should().Be(Token);
    }

    [Fact]
    public void The_same_secret_encrypts_differently_every_time()
    {
        var protector = CreateProtector();

        // A fresh nonce per call. Identical ciphertexts would tell anyone reading the table which
        // tenants share a token, and reusing a nonce under one key breaks GCM outright.
        protector.Protect(Token).Should().NotBe(protector.Protect(Token));
    }

    [Fact]
    public void The_ciphertext_never_contains_the_plaintext()
    {
        CreateProtector().Protect(Token).Should().NotContain(Token);
    }

    [Fact]
    public void A_tampered_ciphertext_is_refused_rather_than_decrypted()
    {
        var protector = CreateProtector();
        var parts = protector.Protect(Token).Split('.');

        // Flip one character of the ciphertext segment. With an unauthenticated mode this would
        // decrypt to different bytes and be used as a bearer token; GCM's tag check must catch it.
        var body = parts[3].ToCharArray();
        body[0] = body[0] == 'A' ? 'B' : 'A';
        parts[3] = new string(body);

        var act = () => protector.Unprotect(string.Join('.', parts));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_secret_encrypted_under_a_retired_key_is_still_readable()
    {
        var keys = new Dictionary<string, string> { ["old"] = NewKey() };

        var before = CreateProtector("old", keys);
        var protectedValue = before.Protect(Token);

        // Rotation: a new active key arrives, the old one stays configured.
        keys["new"] = NewKey();
        var after = CreateProtector("new", keys);

        after.Unprotect(protectedValue).Should().Be(Token);
        after.RequiresRewrap(protectedValue).Should().BeTrue();
        after.RequiresRewrap(after.Protect(Token)).Should().BeFalse();
    }

    [Fact]
    public void A_secret_encrypted_under_an_absent_key_names_the_missing_key()
    {
        var protectedValue = CreateProtector("old", new Dictionary<string, string> { ["old"] = NewKey() })
            .Protect(Token);

        var replacement = CreateProtector("k1");

        var act = () => replacement.Unprotect(protectedValue);

        act.Should().Throw<InvalidOperationException>().WithMessage("*old*");
    }

    [Fact]
    public void A_key_of_the_wrong_length_is_refused_at_construction()
    {
        var act = () => CreateProtector(
            "k1",
            new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(new byte[16]) });

        // Startup, not first use. AES-128 material silently accepted here would be a downgrade
        // nobody notices until the algorithm is being audited.
        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void An_active_key_that_is_not_configured_is_refused_at_construction()
    {
        var act = () => CreateProtector("missing");

        act.Should().Throw<InvalidOperationException>().WithMessage("*missing*");
    }
}
