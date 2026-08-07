using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Security;

/// <summary>Encryption settings, bound from the <c>Security</c> configuration section.</summary>
public sealed class SecurityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Security";

    /// <summary>Required key length for AES-256, in bytes.</summary>
    public const int KeyLengthBytes = 32;

    /// <summary>
    /// Encryption keys, base64-encoded, keyed by an identifier that is written into every
    /// ciphertext.
    /// <para>
    /// More than one may be present. That is what makes rotation possible without downtime: a new
    /// key becomes active while old ciphertexts remain readable under their original key until
    /// they are re-wrapped.
    /// </para>
    /// </summary>
    [MinLength(1)]
    public Dictionary<string, string> EncryptionKeys { get; init; } = [];

    /// <summary>Identifier of the key new secrets are encrypted with.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ActiveKeyId { get; init; } = string.Empty;
}

/// <summary>
/// AES-256-GCM implementation of <see cref="ISecretProtector"/>.
/// <para>
/// GCM rather than CBC because it is authenticated: tampering with a stored ciphertext fails the
/// tag check and throws, instead of silently decrypting to different bytes. For a value that is
/// about to be used as a bearer credential against someone else's API, that distinction matters.
/// </para>
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const char Separator = '.';
    private const int NonceLength = 12; // 96 bits, the size GCM is specified for.
    private const int TagLength = 16;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Encryption settings.</param>
    /// <exception cref="InvalidOperationException">
    /// A key is missing, the wrong length, or the active key is not among those configured. All
    /// are refused at startup rather than at the first send.
    /// </exception>
    public AesGcmSecretProtector(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _keys = [];

        foreach (var (keyId, encoded) in settings.EncryptionKeys)
        {
            byte[] key;

            try
            {
                key = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"Encryption key '{keyId}' is not valid base64.", exception);
            }

            if (key.Length != SecurityOptions.KeyLengthBytes)
            {
                throw new InvalidOperationException(
                    $"Encryption key '{keyId}' must be {SecurityOptions.KeyLengthBytes} bytes for AES-256; "
                    + $"it is {key.Length}.");
            }

            _keys[keyId] = key;
        }

        if (!_keys.ContainsKey(settings.ActiveKeyId))
        {
            throw new InvalidOperationException(
                $"The active encryption key '{settings.ActiveKeyId}' is not present in Security:EncryptionKeys.");
        }

        _activeKeyId = settings.ActiveKeyId;
    }

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var key = _keys[_activeKeyId];

        // A fresh random nonce per encryption. Reusing one under the same key breaks GCM
        // catastrophically - it leaks the XOR of the plaintexts and forges the authentication tag.
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // The key id travels with the ciphertext, so a value encrypted under an old key can still
        // be read after rotation without recording which key was used anywhere else.
        return string.Join(
            Separator,
            Version,
            _activeKeyId,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    /// <inheritdoc />
    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);

        var parts = protectedValue.Split(Separator);

        if (parts.Length != 5 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored secret is not in a recognised format.");
        }

        if (!_keys.TryGetValue(parts[1], out var key))
        {
            // Named deliberately: an operator restoring a backup into an environment without the
            // matching key needs to know which key is missing, and the key id is not a secret.
            throw new InvalidOperationException(
                $"The stored secret was encrypted with key '{parts[1]}', which is not configured here.");
        }

        try
        {
            var nonce = Convert.FromBase64String(parts[2]);
            var ciphertext = Convert.FromBase64String(parts[3]);
            var tag = Convert.FromBase64String(parts[4]);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, TagLength);

            // Throws if the tag does not verify, which is the whole point of using GCM.
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The stored secret could not be decrypted. It may have been altered.", exception);
        }
    }

    /// <inheritdoc />
    public bool RequiresRewrap(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return false;
        }

        var parts = protectedValue.Split(Separator);

        return parts.Length == 5 && !string.Equals(parts[1], _activeKeyId, StringComparison.Ordinal);
    }
}
