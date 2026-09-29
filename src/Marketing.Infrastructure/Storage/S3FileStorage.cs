using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Marketing.Common.Exceptions;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Storage;

/// <summary>
/// Stores files in an S3 bucket.
/// <para>
/// The implementation the interface was written for. <see cref="LocalFileStorage"/> is correct on
/// one machine and quietly wrong on several: a container's filesystem is its own and it does not
/// survive a redeploy, so an export written by one task cannot be downloaded through another, and
/// every file vanishes the next time the service is updated.
/// </para>
/// <para>
/// Keys are generated in exactly the same shape as the local provider's, so the two are
/// interchangeable and a key recorded in the database keeps its meaning if the provider changes.
/// </para>
/// </summary>
public sealed partial class S3FileStorage : IFileStorage
{
    private readonly IAmazonS3 _client;
    private readonly StorageOptions _options;
    private readonly ILogger<S3FileStorage> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="client">S3 client, configured by the DI registration.</param>
    /// <param name="options">Storage settings.</param>
    /// <param name="logger">Logger.</param>
    public S3FileStorage(
        IAmazonS3 client,
        IOptions<StorageOptions> options,
        ILogger<S3FileStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "aws-s3";

    private string Bucket => _options.BucketName!;

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        string container,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(content);

        // Identical to the local provider's, deliberately. A key is opaque to callers, but it is
        // persisted on the job and batch rows, so the two providers agreeing about its shape is
        // what makes switching between them a configuration change rather than a data migration.
        var extension = SafeExtension(fileName);
        var key = $"{SanitiseContainer(container)}/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{extension}";

        // TransferUtility, not PutObject: it switches to a multipart upload above the threshold,
        // which is what keeps a several-hundred-megabyte export from being one request that has
        // to succeed all at once or start again from nothing.
        using var transfer = new TransferUtility(_client);

        await transfer.UploadAsync(
            new TransferUtilityUploadRequest
            {
                BucketName = Bucket,
                Key = Qualify(key),
                InputStream = content,
                PartSize = _options.MultipartThresholdBytes,
                AutoCloseStream = false,

                // At rest by default. The bucket almost certainly enforces this too; saying it
                // here means a bucket that was created without the policy is still encrypted.
                ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            },
            cancellationToken);

        LogSaved(key, content.CanSeek ? content.Length : -1);

        return key;
    }

    /// <inheritdoc />
    public async Task<Stream> OpenAsync(string key, CancellationToken cancellationToken = default)
    {
        var qualified = Qualify(key);

        try
        {
            var response = await _client.GetObjectAsync(Bucket, qualified, cancellationToken);

            // The caller disposes it, as the interface says. The response stream is the network
            // stream, so the body is not buffered into memory to hand back - which is the whole
            // point for a file that may be hundreds of megabytes.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            throw new NotFoundException("Stored file", key);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(Bucket, Qualify(key), cancellationToken);

            return true;
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        // S3 treats deleting an absent key as success, which is the behaviour the interface asks
        // for, so there is nothing to catch here.
        await _client.DeleteObjectAsync(Bucket, Qualify(key), cancellationToken);
    }

    /// <summary>
    /// Applies the configured prefix, refusing any key that would reach outside it.
    /// </summary>
    /// <remarks>
    /// The same guard as the local provider's, for the same reason: keys arrive from request
    /// parameters. S3 has no directories to traverse, but it will happily store and return an
    /// object literally named <c>../other-environment/secrets</c>, and a shared bucket split by
    /// prefix is exactly the arrangement that makes that worth something to an attacker.
    /// </remarks>
    /// <param name="key">Storage key.</param>
    private string Qualify(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.StartsWith('/')
            || key.Contains("..", StringComparison.Ordinal)
            || key.Contains('\\', StringComparison.Ordinal))
        {
            throw new ValidationException("key", "That is not a valid file reference.");
        }

        var prefix = _options.Prefix.Trim('/');

        return prefix.Length == 0 ? key : $"{prefix}/{key}";
    }

    /// <summary>Whether the failure means "no such object" rather than a real error.</summary>
    /// <remarks>
    /// Both codes appear in practice: <c>NoSuchKey</c> from GetObject, and a bare 404 with an
    /// empty error code from HeadObject, because a HEAD response carries no body to put one in.
    /// Treating only the first as missing makes <see cref="ExistsAsync"/> throw instead of
    /// answering false.
    /// </remarks>
    /// <param name="exception">Failure from the SDK.</param>
    private static bool IsMissing(AmazonS3Exception exception) =>
        exception.StatusCode == System.Net.HttpStatusCode.NotFound
        || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

    /// <summary>Returns the upload's extension when it is one we accept, otherwise none.</summary>
    private static string SafeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        return extension is ".csv" or ".xlsx" or ".xls" ? extension : string.Empty;
    }

    private static string SanitiseContainer(string container) =>
        new([.. container.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')]);

    [LoggerMessage(
        EventId = 2911,
        Level = LogLevel.Information,
        Message = "Stored file {Key} in S3 ({ByteCount} bytes).")]
    private partial void LogSaved(string key, long byteCount);
}
