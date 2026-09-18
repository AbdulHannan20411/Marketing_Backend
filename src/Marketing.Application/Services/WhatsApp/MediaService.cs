using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>Files exchanged with customers, held by this platform rather than by Meta.</summary>
public interface IMediaService
{
    /// <summary>Stores a file and uploads it to Meta so a message can reference it.</summary>
    /// <param name="command">The file and what the caller says it is.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MediaAssetResponse> UploadAsync(MediaUploadCommand command, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for streaming back to the caller.</summary>
    /// <param name="mediaId">Public media identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MediaDownload> OpenAsync(string mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file a customer sent into this platform's own storage.
    /// </summary>
    /// <remarks>
    /// Called from the webhook, where a failure must not cost the message itself: a thread showing a
    /// missing attachment is far better than a thread missing the message that carried it.
    /// </remarks>
    /// <param name="metaMediaId">Meta's media identifier from the webhook.</param>
    /// <param name="fileName">File name Meta reported, when it gave one.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored file, or null when it could not be fetched.</returns>
    public Task<MediaAsset?> StoreInboundAsync(
        string metaMediaId,
        string? fileName,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Turns a stored file into the shape the client expects.</summary>
    /// <param name="media">Stored file.</param>
    public MediaAssetResponse ToResponse(MediaAsset media);
}

/// <inheritdoc cref="IMediaService" />
/// <remarks>
/// Every file is stored twice over: once here, and once at Meta, which keeps its own copy for 30
/// days. Only the local copy is served, because Meta's URL expires and carries no tenant check - a
/// six-week-old thread would otherwise render broken images.
/// </remarks>
public sealed partial class MediaService : IMediaService
{
    /// <summary>Storage container holding every tenant's WhatsApp files.</summary>
    private const string Container = "whatsapp-media";

    private readonly IRepository<MediaAsset> _media;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWhatsAppGateway _gateway;
    private readonly IFileStorage _storage;
    private readonly ISecretProtector _protector;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<MediaService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public MediaService(
        IRepository<MediaAsset> media,
        IWhatsAppConnectionRepository connections,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IWhatsAppGateway gateway,
        IFileStorage storage,
        ISecretProtector protector,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<MediaService> logger)
    {
        _media = media;
        _connections = connections;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _gateway = gateway;
        _storage = storage;
        _protector = protector;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MediaAssetResponse> UploadAsync(
        MediaUploadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Validate(command);

        var connection = await _connections.FindForTenantAsync(_tenantContext.RequireTenantId(), cancellationToken);

        if (connection is not { PhoneNumberId: { Length: > 0 } phoneNumberId, EncryptedAccessToken: { Length: > 0 } encrypted })
        {
            throw new BusinessRuleException(
                "not_connected",
                "Connect a WhatsApp account before uploading files.");
        }

        var accessToken = _protector.Unprotect(encrypted);

        // Stored first, then sent. The bytes arrive once and are needed twice, and holding a 100 MB
        // document in memory to serve both would be paid for by every other request on the server.
        var storagePath = await _storage.SaveAsync(Container, command.FileName, command.Content, cancellationToken);

        string? metaMediaId = null;

        try
        {
            await using var upload = await _storage.OpenAsync(storagePath, cancellationToken);

            metaMediaId = await _gateway.UploadMediaAsync(
                phoneNumberId,
                upload,
                command.FileName,
                command.MimeType,
                accessToken,
                cancellationToken);
        }
        catch (ExternalServiceException exception) when (!exception.IsTransient)
        {
            // The copy stays. A campaign cannot use this file until Meta has it, but the agent's
            // upload is not lost, and a retry re-uses the bytes already here.
            LogMetaUploadFailed(exception, command.Kind);
        }

        var media = new MediaAsset
        {
            TenantId = _tenantContext.RequireTenantId(),
            MetaMediaId = metaMediaId,
            Kind = command.Kind,
            FileName = command.FileName,
            MimeType = command.MimeType,
            SizeBytes = command.SizeBytes,
            StoragePath = storagePath,
            UploadedAt = _clock.UtcNow,
        };

        _media.Add(media);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(media);
    }

    /// <inheritdoc />
    public async Task<MediaDownload> OpenAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Media, mediaId, "media");

        // The tenant filter does the access check. Another workspace's id is not found rather than
        // forbidden, so a caller cannot learn that the file exists at all.
        var media = await _queries.FirstOrDefaultAsync(
            _media.Query().Where(asset => asset.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Media", mediaId);

        var content = await _storage.OpenAsync(media.StoragePath, cancellationToken);

        return new MediaDownload(
            content,
            media.MimeType is { Length: > 0 } mimeType ? mimeType : "application/octet-stream",
            media.FileName is { Length: > 0 } fileName ? fileName : mediaId);
    }

    /// <inheritdoc />
    public async Task<MediaAsset?> StoreInboundAsync(
        string metaMediaId,
        string? fileName,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        // A redelivered webhook must not fetch the same bytes twice, nor create a second row.
        var existing = await _queries.FirstOrDefaultAsync(
            _media.Query().Where(asset => asset.MetaMediaId == metaMediaId),
            cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        try
        {
            var download = await _gateway.DownloadMediaAsync(metaMediaId, accessToken, cancellationToken);
            var kind = MediaLimits.KindFor(download.MimeType) ?? MediaKind.Document;
            var name = fileName is { Length: > 0 } given ? given : DefaultFileName(metaMediaId, download.MimeType);

            using var content = new MemoryStream(download.Content, writable: false);

            var storagePath = await _storage.SaveAsync(Container, name, content, cancellationToken);

            var media = new MediaAsset
            {
                TenantId = _tenantContext.TenantId,
                MetaMediaId = metaMediaId,
                Kind = kind,
                FileName = name,
                MimeType = download.MimeType,
                SizeBytes = download.SizeBytes,
                StoragePath = storagePath,
                UploadedAt = _clock.UtcNow,
            };

            _media.Add(media);

            return media;
        }
        catch (Exception exception) when (exception is ExternalServiceException or IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed. The message this file belongs to is written either way; an
            // attachment that could not be fetched leaves a thread with a gap, not without the
            // customer's message.
            LogInboundDownloadFailed(exception, metaMediaId);

            return null;
        }
    }

    /// <inheritdoc />
    public MediaAssetResponse ToResponse(MediaAsset media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var id = PublicId.From(PublicId.Media, media.Id);

        return new MediaAssetResponse(
            id,
            media.Kind,
            media.FileName,
            media.MimeType,
            media.SizeBytes,
            $"/api/v1/whatsapp/media/{id}",
            media.UploadedAt);
    }

    /// <summary>Refuses anything WhatsApp would refuse, before it is stored or sent.</summary>
    private static void Validate(MediaUploadCommand command)
    {
        if (command.FileName is not { Length: > 0 })
        {
            throw new ValidationException("file", "Choose a file to upload.");
        }

        if (!MediaLimits.Accepts(command.Kind, command.MimeType))
        {
            var accepted = string.Join(", ", MediaLimits.MimeTypesFor(command.Kind));

            throw new ValidationException(
                "file",
                $"WhatsApp does not accept {command.MimeType} as {command.Kind.ToString().ToLowerInvariant()}. Accepted: {accepted}.");
        }

        var maximum = MediaLimits.MaximumBytesFor(command.Kind);

        if (command.SizeBytes > maximum)
        {
            throw new ValidationException(
                "file",
                $"{command.Kind} files must be {maximum / (1024 * 1024)} MB or smaller.");
        }
    }

    /// <summary>A name for an inbound file Meta described only by its media type.</summary>
    private static string DefaultFileName(string metaMediaId, string mimeType) =>
        mimeType switch
        {
            "image/jpeg" => $"{metaMediaId}.jpg",
            "image/png" => $"{metaMediaId}.png",
            "video/mp4" => $"{metaMediaId}.mp4",
            "video/3gpp" => $"{metaMediaId}.3gp",
            "audio/aac" => $"{metaMediaId}.aac",
            "audio/mpeg" => $"{metaMediaId}.mp3",
            "audio/mp4" => $"{metaMediaId}.m4a",
            "audio/ogg" => $"{metaMediaId}.ogg",
            "application/pdf" => $"{metaMediaId}.pdf",
            _ => metaMediaId,
        };

    [LoggerMessage(
        EventId = 2730,
        Level = LogLevel.Warning,
        Message = "Meta refused an upload of a {Kind} file; the copy is stored but cannot be sent yet.")]
    private partial void LogMetaUploadFailed(Exception exception, MediaKind kind);

    [LoggerMessage(
        EventId = 2731,
        Level = LogLevel.Warning,
        Message = "Could not download inbound media {MetaMediaId}; the message is kept without its attachment.")]
    private partial void LogInboundDownloadFailed(Exception exception, string metaMediaId);
}
