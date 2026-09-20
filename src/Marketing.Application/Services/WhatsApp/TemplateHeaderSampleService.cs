using System.Net;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>
/// What a file's first bytes say it is, whatever its name or declared type claims.
/// </summary>
/// <remarks>
/// Only the formats Meta accepts for a template header. Anything else is unrecognised, which is the
/// point: a renamed file is caught here rather than refused by Meta after the template is submitted.
/// </remarks>
public static class MediaSignature
{
    /// <summary>Bytes needed to recognise every supported format.</summary>
    public const int HeaderLength = 16;

    /// <summary>The kind and media type the bytes belong to, or null when they are none Meta takes.</summary>
    /// <param name="header">The file's first bytes.</param>
    public static (MediaKind Kind, string MimeType)? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return (MediaKind.Image, "image/jpeg");
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return (MediaKind.Image, "image/png");
        }

        if (header.StartsWith("%PDF-"u8))
        {
            return (MediaKind.Document, "application/pdf");
        }

        // ISO base media: a box size, then "ftyp", then the brand. 3GP brands start "3g"; every other
        // brand in this family is served as MP4.
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return header[8] == (byte)'3' && header[9] == (byte)'g'
                ? (MediaKind.Video, "video/3gpp")
                : (MediaKind.Video, "video/mp4");
        }

        return null;
    }
}

/// <summary>Stores template header example files and hands them to template submission.</summary>
public interface ITemplateHeaderSampleService
{
    /// <summary>Checks and stores an example file.</summary>
    /// <param name="upload">The file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<TemplateHeaderSampleResponse> UploadAsync(
        TemplateHeaderSampleUpload upload,
        CancellationToken cancellationToken = default);

    /// <summary>Streams a stored example back, for the editor's preview.</summary>
    /// <param name="sampleId">Public sample id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MediaDownload> OpenAsync(string sampleId, CancellationToken cancellationToken = default);

    /// <summary>Loads a sample for submission, refusing one of the wrong kind.</summary>
    /// <param name="sampleId">Public sample id.</param>
    /// <param name="headerKind">The header it is for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracked sample and its bytes.</returns>
    public Task<(TemplateHeaderSample Sample, byte[] Content)> LoadForSubmitAsync(
        string sampleId,
        TemplateHeaderKind headerKind,
        CancellationToken cancellationToken = default);

    /// <summary>Projects samples by id, for template responses.</summary>
    /// <param name="sampleIds">Internal sample ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyDictionary<long, TemplateHeaderSampleResponse>> DescribeAsync(
        IEnumerable<long?> sampleIds,
        CancellationToken cancellationToken = default);

    /// <summary>Removes samples never attached to a template within a day, across every workspace.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many were removed.</returns>
    public Task<int> RemoveAbandonedAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITemplateHeaderSampleService" />
public sealed partial class TemplateHeaderSampleService : ITemplateHeaderSampleService
{
    private const string Container = "template-header-samples";

    /// <summary>Samples removed per clean-up pass.</summary>
    private const int CleanupBatch = 200;

    private readonly IRepository<TemplateHeaderSample> _samples;
    private readonly IFileStorage _storage;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<TemplateHeaderSampleService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public TemplateHeaderSampleService(
        IRepository<TemplateHeaderSample> samples,
        IFileStorage storage,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<TemplateHeaderSampleService> logger)
    {
        _samples = samples;
        _storage = storage;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TemplateHeaderSampleResponse> UploadAsync(
        TemplateHeaderSampleUpload upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var claimed = upload.Kind?.Trim().ToLowerInvariant() switch
        {
            "image" => MediaKind.Image,
            "video" => MediaKind.Video,
            "document" => MediaKind.Document,
            _ => throw new ValidationException("kind", "The header example must be an image, a video or a document."),
        };

        var limit = MediaLimits.MaximumBytesFor(claimed);

        if (upload.SizeBytes > limit)
        {
            throw new RequestRejectedException(
                HttpStatusCode.RequestEntityTooLarge,
                "file_too_large",
                $"A {Describe(claimed)} header example can be {limit / (1024 * 1024)} MB at most.",
                "file");
        }

        var content = await BufferAsync(upload.Content, cancellationToken);
        var detected = MediaSignature.Detect(content.AsSpan(0, Math.Min(content.Length, MediaSignature.HeaderLength)));

        // The bytes decide, not the name or the declared type: a PNG renamed .mp4 is a PNG.
        if (detected is not { } actual)
        {
            throw new RequestRejectedException(
                HttpStatusCode.UnsupportedMediaType,
                "unsupported_media_type",
                "Use a JPEG or PNG image, an MP4 or 3GP video, or a PDF document.",
                "file");
        }

        if (actual.Kind != claimed)
        {
            throw new RequestRejectedException(
                HttpStatusCode.UnprocessableEntity,
                "kind_mismatch",
                $"That file is {Article(actual.Kind)} {Describe(actual.Kind)}, but the header is {Article(claimed)} {Describe(claimed)}.",
                "kind");
        }

        var name = Path.GetFileName(upload.FileName.Trim());
        name = name.Length == 0 ? $"example.{Extension(actual.MimeType)}" : name.Length <= 255 ? name : name[^255..];

        using var stored = new MemoryStream(content, writable: false);
        var path = await _storage.SaveAsync(Container, name, stored, cancellationToken);

        var sample = new TemplateHeaderSample
        {
            TenantId = _tenantContext.RequireTenantId(),
            Kind = actual.Kind,
            FileName = name,
            MimeType = actual.MimeType,
            SizeBytes = content.LongLength,
            StoragePath = path,
            UploadedAt = _clock.UtcNow,
        };

        _samples.Add(sample);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(sample);
    }

    /// <inheritdoc />
    public async Task<MediaDownload> OpenAsync(string sampleId, CancellationToken cancellationToken = default)
    {
        var sample = await FindAsync(sampleId, tracked: false, cancellationToken);

        return new MediaDownload(await _storage.OpenAsync(sample.StoragePath, cancellationToken), sample.MimeType, sample.FileName);
    }

    /// <inheritdoc />
    public async Task<(TemplateHeaderSample Sample, byte[] Content)> LoadForSubmitAsync(
        string sampleId,
        TemplateHeaderKind headerKind,
        CancellationToken cancellationToken = default)
    {
        if (!PublicId.TryParse(PublicId.TemplateHeaderSample, sampleId, out var id))
        {
            throw new ValidationException("headerSampleId", "Upload the header's example file again.");
        }

        var sample = await _queries.FirstOrDefaultAsync(
            _samples.Query(asNoTracking: false).Where(candidate => candidate.Id == id),
            cancellationToken)
            ?? throw new ValidationException("headerSampleId", "That example file is no longer available. Upload it again.");

        var wanted = headerKind switch
        {
            TemplateHeaderKind.Image => MediaKind.Image,
            TemplateHeaderKind.Video => MediaKind.Video,
            TemplateHeaderKind.Document => MediaKind.Document,
            _ => (MediaKind?)null,
        };

        if (sample.Kind != wanted)
        {
            throw new ValidationException(
                "headerSampleId",
                $"The example file is {Article(sample.Kind)} {Describe(sample.Kind)} but the header is "
                + $"{headerKind.ToString().ToLowerInvariant()}.");
        }

        await using var stream = await _storage.OpenAsync(sample.StoragePath, cancellationToken);

        return (sample, await BufferAsync(stream, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, TemplateHeaderSampleResponse>> DescribeAsync(
        IEnumerable<long?> sampleIds,
        CancellationToken cancellationToken = default)
    {
        var ids = sampleIds.OfType<long>().Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<long, TemplateHeaderSampleResponse>();
        }

        var samples = await _queries.ToListAsync(
            _samples.Query().Where(sample => ids.Contains(sample.Id)),
            cancellationToken);

        return samples.ToDictionary(sample => sample.Id, ToResponse);
    }

    /// <inheritdoc />
    public async Task<int> RemoveAbandonedAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.UtcNow.AddHours(-24);

        // Across workspaces: the job has no tenant, and a sample carries its own.
        var abandoned = await _queries.ToListAsync(
            _samples.Query(asNoTracking: false)
                .IgnoreQueryFilters()
                .Where(sample => !sample.IsDeleted && sample.MessageTemplateId == null && sample.UploadedAt < cutoff)
                .OrderBy(sample => sample.UploadedAt)
                .Take(CleanupBatch),
            cancellationToken);

        foreach (var sample in abandoned)
        {
            try
            {
                await _storage.DeleteAsync(sample.StoragePath, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The row still goes: a stray file costs disk, a stray row keeps the job retrying it.
                LogFileNotRemoved(exception, sample.Id);
            }

            _samples.Remove(sample);
        }

        if (abandoned.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return abandoned.Count;
    }

    private async Task<TemplateHeaderSample> FindAsync(string sampleId, bool tracked, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.TemplateHeaderSample, sampleId, "header example");

        // The tenant filter does the access check: another workspace's sample is simply not found.
        return await _queries.FirstOrDefaultAsync(
            _samples.Query(asNoTracking: !tracked).Where(sample => sample.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Header example", sampleId);
    }

    private static TemplateHeaderSampleResponse ToResponse(TemplateHeaderSample sample)
    {
        var id = PublicId.From(PublicId.TemplateHeaderSample, sample.Id);

        return new TemplateHeaderSampleResponse(
            id,
            sample.Kind,
            sample.FileName,
            sample.MimeType,
            sample.SizeBytes,
            $"/api/v1/templates/header-samples/{id}/content",
            sample.UploadedAt);
    }

    /// <summary>Reads a stream whole. Samples are bounded by the size check before this runs.</summary>
    private static async Task<byte[]> BufferAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        await content.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }

    private static string Describe(MediaKind kind) => kind.ToString().ToLowerInvariant();

    private static string Article(MediaKind kind) => kind == MediaKind.Image ? "an" : "a";

    private static string Extension(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "video/3gpp" => "3gp",
        "video/mp4" => "mp4",
        _ => "pdf",
    };

    [LoggerMessage(
        EventId = 2630,
        Level = LogLevel.Warning,
        Message = "Could not delete the file of abandoned header example {SampleId}; the record was removed.")]
    private partial void LogFileNotRemoved(Exception exception, long sampleId);
}
