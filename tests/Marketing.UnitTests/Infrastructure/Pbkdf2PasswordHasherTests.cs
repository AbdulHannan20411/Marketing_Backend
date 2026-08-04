using AwesomeAssertions;
using Marketing.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

public sealed class Pbkdf2PasswordHasherTests
{
    // Deliberately low so the suite stays fast; production uses the configured default.
    private static Pbkdf2PasswordHasher CreateHasher(int iterations = 100_000) =>
        new(Options.Create(new PasswordHashingOptions { Iterations = iterations }));

    [Fact]
    public void Hashing_the_same_password_twice_produces_different_hashes()
    {
        var hasher = CreateHasher();

        var first = hasher.Hash("Correct horse battery staple");
        var second = hasher.Hash("Correct horse battery staple");

        // Distinct salts. Identical hashes would let an attacker spot shared passwords across
        // accounts straight from a database dump.
        first.Should().NotBe(second);
    }

    [Fact]
    public void A_correct_password_verifies()
    {
        var hasher = CreateHasher();
        var hash = hasher.Hash("Correct horse battery staple");

        var (isValid, requiresRehash) = hasher.Verify("Correct horse battery staple", hash);

        isValid.Should().BeTrue();
        requiresRehash.Should().BeFalse();
    }

    [Fact]
    public void An_incorrect_password_does_not_verify()
    {
        var hasher = CreateHasher();
        var hash = hasher.Hash("Correct horse battery staple");

        var (isValid, _) = hasher.Verify("correct horse battery staple", hash);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void A_hash_written_with_a_lower_work_factor_is_flagged_for_upgrade()
    {
        var legacy = CreateHasher(100_000).Hash("Correct horse battery staple");
        var current = CreateHasher(150_000);

        var (isValid, requiresRehash) = current.Verify("Correct horse battery staple", legacy);

        // Still valid - raising the work factor must never lock existing users out - but marked so
        // the sign-in path can re-hash while it still holds the plaintext.
        isValid.Should().BeTrue();
        requiresRehash.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("bcrypt$10$salt$hash")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$!!!not-base64!!!$aGFzaA==")]
    public void A_malformed_stored_hash_is_rejected_without_throwing(string storedHash)
    {
        var hasher = CreateHasher();

        var (isValid, requiresRehash) = hasher.Verify("any password", storedHash);

        // A corrupt row must fail the sign-in, not crash the endpoint.
        isValid.Should().BeFalse();
        requiresRehash.Should().BeFalse();
    }

    [Fact]
    public void Verification_of_an_empty_password_fails_rather_than_throwing()
    {
        var hasher = CreateHasher();
        var hash = hasher.Hash("Correct horse battery staple");

        hasher.Verify(string.Empty, hash).IsValid.Should().BeFalse();
    }
}
