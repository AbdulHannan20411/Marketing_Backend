using System.Globalization;
using System.Security.Cryptography;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Authentication;

/// <summary>
/// PBKDF2-HMAC-SHA256 implementation of <see cref="IPasswordHasher"/>.
/// <para>
/// The iteration count is stored inside each hash rather than assumed, so raising the work factor
/// is a configuration change: existing users keep signing in against their original parameters and
/// are transparently upgraded on their next successful sign-in.
/// </para>
/// <para>
/// PBKDF2 is chosen over Argon2id only because it is in the framework and needs no native
/// dependency. Argon2id resists GPU attack considerably better and is the intended upgrade; the
/// versioned hash prefix exists so that migration can be a new algorithm tag rather than a forced
/// password reset.
/// </para>
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string AlgorithmTag = "pbkdf2-sha256";
    private const int SaltByteLength = 16;
    private const int SubkeyByteLength = 32;
    private const char FieldSeparator = '$';

    private readonly PasswordHashingOptions _options;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Work-factor settings.</param>
    public Pbkdf2PasswordHasher(IOptions<PasswordHashingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltByteLength);

        var subkey = KeyDerivation.Pbkdf2(
            password,
            salt,
            KeyDerivationPrf.HMACSHA256,
            _options.Iterations,
            SubkeyByteLength);

        return string.Join(
            FieldSeparator,
            AlgorithmTag,
            _options.Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(subkey));
    }

    /// <inheritdoc />
    public (bool IsValid, bool RequiresRehash) Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
        {
            return (false, false);
        }

        var parts = passwordHash.Split(FieldSeparator);

        if (parts.Length != 4 || !string.Equals(parts[0], AlgorithmTag, StringComparison.Ordinal))
        {
            return (false, false);
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) ||
            iterations <= 0)
        {
            return (false, false);
        }

        byte[] salt;
        byte[] expectedSubkey;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedSubkey = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return (false, false);
        }

        var actualSubkey = KeyDerivation.Pbkdf2(
            password,
            salt,
            KeyDerivationPrf.HMACSHA256,
            iterations,
            expectedSubkey.Length);

        // Constant-time comparison: a byte-by-byte early exit leaks how much of the derived key
        // matched, which is enough to recover it one byte at a time.
        var isValid = CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);

        return (isValid, isValid && iterations < _options.Iterations);
    }
}
