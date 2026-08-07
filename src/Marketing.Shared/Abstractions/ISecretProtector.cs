namespace Marketing.Shared.Abstractions;

/// <summary>
/// Encrypts and decrypts secrets held in the database - today, tenants' Meta access tokens.
/// <para>
/// A Meta token is a live credential: whoever holds it can send messages as that business and read
/// its data. Storing it in plaintext would mean a single database disclosure compromises every
/// connected customer at once, so it is encrypted at rest with a key that lives somewhere the
/// database backup does not.
/// </para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a value for storage.</summary>
    /// <param name="plaintext">The secret.</param>
    /// <returns>An opaque, self-describing string safe to persist.</returns>
    public string Protect(string plaintext);

    /// <summary>Decrypts a stored value.</summary>
    /// <param name="protectedValue">Value previously returned by <see cref="Protect"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// The value is malformed, was encrypted with a key this deployment does not have, or has been
    /// tampered with. All three are refused rather than returning something plausible.
    /// </exception>
    public string Unprotect(string protectedValue);

    /// <summary>
    /// Whether a stored value was encrypted with a key that is no longer the active one, and so
    /// should be re-wrapped the next time it is written.
    /// </summary>
    /// <param name="protectedValue">Stored value.</param>
    public bool RequiresRewrap(string protectedValue);
}
