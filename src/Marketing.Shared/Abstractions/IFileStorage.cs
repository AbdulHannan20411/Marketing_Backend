namespace Marketing.Shared.Abstractions;

/// <summary>
/// Stores uploaded and generated files outside the database.
/// <para>
/// A seam, like the payment and email gateways. Import files and error reports are read once by a
/// worker and downloaded once by an operator; putting multi-megabyte blobs in PostgreSQL would
/// bloat every backup and every replica for data with no relational value.
/// </para>
/// <para>
/// Implementations must treat the key as opaque and must not let one escape its container — a key
/// arriving from a request is attacker-controlled, and <c>../</c> in a path is the oldest bug in
/// file handling.
/// </para>
/// </summary>
public interface IFileStorage
{
    /// <summary>Name of the backing store, recorded in logs.</summary>
    public string ProviderName { get; }

    /// <summary>
    /// Saves a stream and returns the key it can be read back with.
    /// </summary>
    /// <param name="container">Logical folder, for example <c>contact-imports</c>.</param>
    /// <param name="fileName">Original file name; only its extension is trusted.</param>
    /// <param name="content">Stream to save. The caller owns it and disposes it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An opaque key. Never a path the caller can reason about.</returns>
    public Task<string> SaveAsync(
        string container,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stored file for reading.
    /// </summary>
    /// <param name="key">Key returned by <see cref="SaveAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A readable stream the caller disposes.</returns>
    /// <exception cref="Common.Exceptions.NotFoundException">No file is stored under that key.</exception>
    public Task<Stream> OpenAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Whether a file exists under a key.</summary>
    /// <param name="key">Key to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a stored file. Deleting something already gone is not an error.
    /// </summary>
    /// <param name="key">Key to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
