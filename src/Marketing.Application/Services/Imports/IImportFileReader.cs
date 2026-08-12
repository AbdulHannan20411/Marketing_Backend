namespace Marketing.Application.Services.Imports;

/// <summary>
/// Reads a tabular upload row by row.
/// <para>
/// An abstraction over CSV and XLSX so the worker holds one parsing path. It yields rows lazily:
/// the worker stages each row as it arrives rather than materialising the whole file, which is what
/// keeps a fifty-thousand-row import from sitting on the large object heap in its entirety.
/// </para>
/// </summary>
public interface IImportFileReader
{
    /// <summary>Whether this reader handles a given file extension.</summary>
    /// <param name="extension">Lower-case extension including the dot.</param>
    public bool Handles(string extension);

    /// <summary>
    /// Reads the header row.
    /// </summary>
    /// <param name="content">The file. The caller owns and disposes it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Column names in file order, trimmed.</returns>
    public Task<IReadOnlyList<string>> ReadHeaderAsync(
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the data rows, excluding the header.
    /// </summary>
    /// <param name="content">The file, positioned at the start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each row's cells, positionally aligned with the header.</returns>
    public IAsyncEnumerable<IReadOnlyList<string>> ReadRowsAsync(
        Stream content,
        CancellationToken cancellationToken = default);
}

/// <summary>Picks the reader that handles a file.</summary>
public interface IImportFileReaderFactory
{
    /// <summary>Returns the reader for an extension.</summary>
    /// <param name="fileName">File name; only the extension is read.</param>
    /// <exception cref="Common.Exceptions.ValidationException">No reader handles it.</exception>
    public IImportFileReader For(string fileName);

    /// <summary>Extensions any reader handles, for the upload validator and the error message.</summary>
    public IReadOnlyList<string> SupportedExtensions { get; }
}
