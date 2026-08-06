namespace Marketing.Shared.Abstractions;

/// <summary>Hashes and verifies user passwords.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a password with a fresh random salt.</summary>
    /// <param name="password">Plaintext password.</param>
    /// <returns>An opaque, self-describing hash string safe to persist.</returns>
    public string Hash(string password);

    /// <summary>
    /// Verifies a password against a stored hash in constant time.
    /// </summary>
    /// <param name="password">Plaintext password supplied at sign-in.</param>
    /// <param name="passwordHash">Stored hash.</param>
    /// <returns>
    /// Whether the password matched, and whether the stored hash used outdated parameters and
    /// should be transparently upgraded on this successful sign-in.
    /// </returns>
    public (bool IsValid, bool RequiresRehash) Verify(string password, string passwordHash);
}
