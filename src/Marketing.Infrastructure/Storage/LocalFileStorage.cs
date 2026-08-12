using System.ComponentModel.DataAnnotations;
using Marketing.Common.Exceptions;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Storage;

/// <summary>File storage settings, bound from the <c>Storage</c> configuration section.</summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Directory files are written under. Relative paths resolve against the content root.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string RootPath { get; init; } = "App_Data/storage";
}

/// <summary>
/// Stores files on the local disk.
/// <para>
/// Correct for a single instance and for development. A multi-instance deployment needs shared
/// storage — the same interface over blob storage or S3 — because one instance cannot read a file
/// another instance wrote to its own disk, and the import worker will not be on the node that
/// handled the upload.
/// </para>
/// </summary>
public sealed partial class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileStorage> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Storage settings.</param>
    /// <param name="environment">Host environment, used to resolve a relative root.</param>
    /// <param name="logger">Logger.</param>
    public LocalFileStorage(
        IOptions<StorageOptions> options,
        IHostEnvironment environment,
        ILogger<LocalFileStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = options.Value.RootPath;

        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);

        Directory.CreateDirectory(_root);

        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "local-disk";

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        string container,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(content);

        // The stored name is generated, never derived from the upload. An operator-supplied name
        // can contain a path, a device name, or characters the file system treats specially; the
        // original is kept on the batch row for display and nowhere else.
        var extension = SafeExtension(fileName);
        var key = $"{SanitiseContainer(container)}/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{extension}";
        var destination = ResolveWithinRoot(key);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var file = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        LogSaved(key, content.CanSeek ? content.Length : -1);

        return key;
    }

    /// <inheritdoc />
    public Task<Stream> OpenAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolveWithinRoot(key);

        if (!File.Exists(path))
        {
            throw new NotFoundException("Stored file", key);
        }

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult(stream);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolveWithinRoot(key)));

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolveWithinRoot(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns a key into a full path, refusing anything that would land outside the root.
    /// </summary>
    /// <remarks>
    /// The guard that matters. Keys reach this from request parameters, and a key containing
    /// <c>../</c> would otherwise read or delete arbitrary files. Compared after full resolution,
    /// because the traversal can be encoded in ways a string check misses.
    /// </remarks>
    /// <param name="key">Storage key.</param>
    private string ResolveWithinRoot(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var combined = Path.GetFullPath(Path.Combine(_root, key));
        var root = Path.GetFullPath(_root);

        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, root, StringComparison.Ordinal))
        {
            throw new Common.Exceptions.ValidationException("key", "That is not a valid file reference.");
        }

        return combined;
    }

    /// <summary>Returns the upload's extension when it is one we accept, otherwise none.</summary>
    private static string SafeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        return extension is ".csv" or ".xlsx" or ".xls" ? extension : string.Empty;
    }

    private static string SanitiseContainer(string container) =>
        new([.. container.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')]);

    [LoggerMessage(
        EventId = 2901,
        Level = LogLevel.Information,
        Message = "Stored file {Key} ({ByteCount} bytes).")]
    private partial void LogSaved(string key, long byteCount);
}
