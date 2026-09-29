using Amazon.S3;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Storage;

/// <summary>
/// Reports whether the configured bucket is reachable and this task is allowed to use it.
/// </summary>
/// <remarks>
/// Worth its own check because the commonest S3 failure on ECS is not S3 being down - it is a task
/// role that was changed, or a bucket policy that no longer grants this environment's prefix. That
/// failure is invisible until somebody tries to download an export, which is hours later and looks
/// like a bug in exports.
/// <para>
/// Reported as degraded and deliberately outside the readiness set, for the same reason the cache
/// is: uploads and downloads stop working, but every other screen in the product still serves, and
/// taking instances out of rotation would turn a feature outage into a site outage.
/// </para>
/// </remarks>
public sealed class S3StorageHealthCheck : IHealthCheck
{
    private readonly IAmazonS3 _client;
    private readonly StorageOptions _options;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="client">S3 client.</param>
    /// <param name="options">Storage settings.</param>
    public S3StorageHealthCheck(IAmazonS3 client, IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var bucket = _options.BucketName ?? string.Empty;

        try
        {
            // A metadata read, not a write. It proves the bucket resolves and the credentials are
            // accepted without leaving an object behind on every probe.
            await _client.GetBucketLocationAsync(bucket, cancellationToken);

            return HealthCheckResult.Healthy($"Bucket '{bucket}' is reachable.");
        }
        catch (AmazonS3Exception exception)
        {
            return HealthCheckResult.Degraded(
                $"Bucket '{bucket}' is not usable: {exception.ErrorCode}.",
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded($"Bucket '{bucket}' could not be reached.", exception);
        }
    }
}
