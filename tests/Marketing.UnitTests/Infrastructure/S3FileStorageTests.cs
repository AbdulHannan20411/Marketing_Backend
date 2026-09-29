using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Marketing.Common.Exceptions;
using Marketing.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Covers the parts of S3 storage that are decisions rather than SDK calls: the prefix guard,
/// and turning "no such object" into the not-found the interface promises.
/// </summary>
public sealed class S3FileStorageTests
{
    private readonly IAmazonS3 _client = Substitute.For<IAmazonS3>();

    private static StorageOptions Options(string prefix = "production") => new()
    {
        Provider = StorageProvider.S3,
        BucketName = "marketing-files",
        Prefix = prefix,
    };

    private S3FileStorage Create(StorageOptions? options = null) =>
        new(_client, Microsoft.Extensions.Options.Options.Create(options ?? Options()), NullLogger<S3FileStorage>.Instance);

    private static AmazonS3Exception Missing(string? code = "NoSuchKey") =>
        new("not found") { StatusCode = System.Net.HttpStatusCode.NotFound, ErrorCode = code };

    [Theory]
    [InlineData("../other-environment/secrets")]
    [InlineData("exports/../../staging/file.csv")]
    [InlineData("/absolute/key")]
    [InlineData("exports\\windows\\style")]
    public async Task A_key_that_reaches_outside_the_prefix_is_refused(string key)
    {
        // S3 has no directories to traverse, but it will happily store and return an object
        // literally named "../staging/file" - and a bucket shared between environments by prefix
        // is precisely the arrangement that makes that worth something to an attacker.
        var storage = Create();

        await Assert.ThrowsAsync<ValidationException>(() => storage.OpenAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_configured_prefix_is_applied_to_the_key()
    {
        _client.GetObjectAsync("marketing-files", "production/exports/file.csv", Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3]) });

        var storage = Create();

        await using var stream = await storage.OpenAsync("exports/file.csv", TestContext.Current.CancellationToken);

        // Asserted through the substitute's argument matcher above: a different prefix, or none,
        // would not have matched and the call would have returned null.
        stream.Should().NotBeNull();
    }

    [Fact]
    public async Task An_empty_prefix_leaves_the_key_alone()
    {
        _client.GetObjectAsync("marketing-files", "exports/file.csv", Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = new MemoryStream() });

        var storage = Create(Options(prefix: string.Empty));

        var stream = await storage.OpenAsync("exports/file.csv", TestContext.Current.CancellationToken);

        await stream.DisposeAsync();
    }

    [Fact]
    public async Task A_missing_object_is_a_not_found_rather_than_an_S3_exception()
    {
        // The interface promises NotFoundException. Letting AmazonS3Exception escape would turn a
        // deleted export into a 500 instead of the 404 the download endpoint relies on.
        _client.GetObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Missing());

        var storage = Create();

        await Assert.ThrowsAsync<NotFoundException>(() => storage.OpenAsync("exports/gone.csv", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_head_request_with_no_error_code_still_counts_as_missing()
    {
        // HeadObject answers 404 with an empty error code, because a HEAD response carries no
        // body to put one in. Matching only on "NoSuchKey" makes Exists throw instead of
        // answering false - which is how an expiry sweep starts failing instead of skipping.
        _client.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Missing(code: null));

        var storage = Create();

        (await storage.ExistsAsync("exports/gone.csv", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task An_object_that_exists_reports_true()
    {
        _client.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse());

        (await Create().ExistsAsync("exports/file.csv", TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public void The_provider_names_itself_for_the_logs()
    {
        Create().ProviderName.Should().Be("aws-s3");
    }

    [Fact]
    public void S3_without_a_bucket_fails_validation_rather_than_start_up()
    {
        // Caught by ValidateDataAnnotations at start-up. The alternative is an API that starts
        // cleanly and throws on the first upload, hours later.
        var options = new StorageOptions { Provider = StorageProvider.S3, BucketName = null };

        var results = options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options));

        results.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("Storage:BucketName");
    }

    [Fact]
    public void Local_storage_needs_no_bucket()
    {
        var options = new StorageOptions { Provider = StorageProvider.Local };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options))
            .Should().BeEmpty();
    }
}
