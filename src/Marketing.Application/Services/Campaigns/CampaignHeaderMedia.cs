using System.Net;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Campaigns;

/// <summary>
/// The image, video or document a media-header template sends with every message of a campaign.
/// </summary>
public interface ICampaignHeaderMedia
{
    /// <summary>
    /// Checks the media a draft names against its template, and returns its key.
    /// </summary>
    /// <param name="template">The campaign's template.</param>
    /// <param name="headerMediaId">Public media id, <c>med_…</c>, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The media's key, or null when the template needs none.</returns>
    public Task<long?> ValidateAsync(MessageTemplate template, string? headerMediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-checks a saved campaign before it is scheduled or sent: the template may have been replaced
    /// since the draft was saved.
    /// </summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task EnsureStillValidAsync(Campaign campaign, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes sure Meta holds the campaign's header media for the sending number, uploading it once
    /// per run and reusing it for every recipient.
    /// </summary>
    /// <param name="campaign">The campaign, tracked; the upload is recorded on it.</param>
    /// <param name="template">Its template.</param>
    /// <param name="phoneNumberId">The sending number.</param>
    /// <param name="accessToken">The number's token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// What each message's header carries; null when the template has no media header. Throws
    /// <see cref="BusinessRuleException"/> when the media cannot be sent, so no message goes without it.
    /// </returns>
    public Task<MetaHeaderMedia?> PrepareForRunAsync(
        Campaign campaign,
        MessageTemplate template,
        string phoneNumberId,
        string? accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Describes campaigns' header media for their responses.</summary>
    /// <param name="tenantId">Workspace the media must belong to.</param>
    /// <param name="mediaIds">Internal media keys.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyDictionary<long, MediaAssetResponse>> DescribeAsync(
        long tenantId,
        IEnumerable<long?> mediaIds,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICampaignHeaderMedia" />
public sealed partial class CampaignHeaderMedia : ICampaignHeaderMedia
{
    /// <summary>
    /// Age after which an uploaded copy is replaced. Meta keeps media for thirty days; five days' margin
    /// stops a run that starts on day twenty-nine from failing halfway through.
    /// </summary>
    private static readonly TimeSpan ReuseFor = TimeSpan.FromDays(25);

    /// <summary>What Meta accepts in a template header, which is narrower than a free-form message.</summary>
    private static readonly Dictionary<MediaKind, string[]> HeaderTypes = new()
    {
        [MediaKind.Image] = ["image/jpeg", "image/png"],
        [MediaKind.Video] = ["video/mp4", "video/3gpp"],
        [MediaKind.Document] = ["application/pdf"],
    };

    private readonly IRepository<MediaAsset> _media;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IQueryExecutor _queries;
    private readonly IFileStorage _storage;
    private readonly IWhatsAppGateway _gateway;
    private readonly IMediaService _mediaService;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<CampaignHeaderMedia> _logger;

    /// <summary>Initialises a new instance.</summary>
    public CampaignHeaderMedia(
        IRepository<MediaAsset> media,
        IRepository<MessageTemplate> templates,
        IQueryExecutor queries,
        IFileStorage storage,
        IWhatsAppGateway gateway,
        IMediaService mediaService,
        IDateTimeProvider clock,
        ILogger<CampaignHeaderMedia> logger)
    {
        _media = media;
        _templates = templates;
        _queries = queries;
        _storage = storage;
        _gateway = gateway;
        _mediaService = mediaService;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long?> ValidateAsync(
        MessageTemplate template,
        string? headerMediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        var needed = KindFor(template.HeaderKind);
        var given = string.IsNullOrWhiteSpace(headerMediaId) ? null : headerMediaId.Trim();

        if (needed is null)
        {
            return given is null
                ? null
                : throw Rejected(
                    "header_media_not_allowed",
                    $"\"{template.Name}\" has no image, video or document header, so the campaign needs no header file.");
        }

        if (given is null)
        {
            throw Rejected(
                "header_media_required",
                $"\"{template.Name}\" has {Article(needed.Value)} {Describe(needed.Value)} header. Choose the "
                + $"{Describe(needed.Value)} to send with every message.");
        }

        var id = PublicId.Parse(PublicId.Media, given, "media");

        // The tenant filter makes another workspace's file simply not found.
        var media = await _queries.FirstOrDefaultAsync(_media.Query().Where(asset => asset.Id == id), cancellationToken)
                    ?? throw new NotFoundException("Media", given);

        Check(template, needed.Value, media);

        return media.Id;
    }

    /// <inheritdoc />
    public async Task EnsureStillValidAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        if (campaign.MessageTemplateId is not { } templateId)
        {
            return;
        }

        var template = await _queries.FirstOrDefaultAsync(
            _templates.Query().Where(candidate => candidate.Id == templateId),
            cancellationToken);

        if (template is not null)
        {
            await ValidateAsync(template, PublicId.FromNullable(PublicId.Media, campaign.HeaderMediaId), cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<MetaHeaderMedia?> PrepareForRunAsync(
        Campaign campaign,
        MessageTemplate template,
        string phoneNumberId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(template);

        if (KindFor(template.HeaderKind) is not { } kind)
        {
            return null;
        }

        var word = Describe(kind);

        var media = campaign.HeaderMediaId is { } mediaId
            ? await _queries.FirstOrDefaultAsync(
                _media.Query().IgnoreQueryFilters()
                    .Where(asset => asset.Id == mediaId && asset.TenantId == campaign.TenantId && !asset.IsDeleted),
                cancellationToken)
            : null;

        if (media is null)
        {
            throw new BusinessRuleException(
                "header_media_required",
                $"\"{template.Name}\" needs {Article(kind)} {word} in its header. Choose one for the campaign and start it again.");
        }

        var now = _clock.UtcNow;

        var reusable = campaign.HeaderMetaMediaId is { Length: > 0 }
                       && string.Equals(campaign.HeaderMetaMediaPhoneNumberId, phoneNumberId, StringComparison.Ordinal)
                       && campaign.HeaderMetaMediaUploadedAt is { } uploadedAt
                       && now - uploadedAt < ReuseFor;

        if (!reusable)
        {
            if (accessToken is null)
            {
                throw CouldNotSend(word);
            }

            try
            {
                await using var content = await _storage.OpenAsync(media.StoragePath, cancellationToken);

                campaign.HeaderMetaMediaId = await _gateway.UploadMediaAsync(
                    phoneNumberId, content, media.FileName, media.MimeType, accessToken, cancellationToken);
                campaign.HeaderMetaMediaPhoneNumberId = phoneNumberId;
                campaign.HeaderMetaMediaUploadedAt = now;
            }
            catch (Exception exception) when (exception is ExternalServiceException or IOException)
            {
                LogUploadFailed(exception, campaign.Id);

                throw CouldNotSend(word);
            }
        }

        return new MetaHeaderMedia(word, campaign.HeaderMetaMediaId!, kind == MediaKind.Document ? media.FileName : null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, MediaAssetResponse>> DescribeAsync(
        long tenantId,
        IEnumerable<long?> mediaIds,
        CancellationToken cancellationToken = default)
    {
        var ids = mediaIds.OfType<long>().Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<long, MediaAssetResponse>();
        }

        var media = await _queries.ToListAsync(
            _media.Query().IgnoreQueryFilters()
                .Where(asset => ids.Contains(asset.Id) && asset.TenantId == tenantId && !asset.IsDeleted),
            cancellationToken);

        return media.ToDictionary(asset => asset.Id, _mediaService.ToResponse);
    }

    private static void Check(MessageTemplate template, MediaKind needed, MediaAsset media)
    {
        if (media.Kind != needed || !HeaderTypes[needed].Contains(media.MimeType, StringComparer.OrdinalIgnoreCase))
        {
            throw Rejected(
                "header_media_kind_mismatch",
                $"\"{template.Name}\" needs {Article(needed)} {Describe(needed)} header ("
                + $"{string.Join(", ", HeaderTypes[needed])}), and this file is {media.MimeType}.");
        }

        var limit = MediaLimits.MaximumBytesFor(needed);

        if (media.SizeBytes > limit)
        {
            throw Rejected(
                "header_media_too_large",
                $"A {Describe(needed)} header can be {limit / (1024 * 1024)} MB at most. Choose a smaller file.");
        }
    }

    private static MediaKind? KindFor(TemplateHeaderKind kind) => kind switch
    {
        TemplateHeaderKind.Image => MediaKind.Image,
        TemplateHeaderKind.Video => MediaKind.Video,
        TemplateHeaderKind.Document => MediaKind.Document,
        _ => null,
    };

    private static RequestRejectedException Rejected(string code, string message) =>
        new(HttpStatusCode.UnprocessableEntity, code, message, "headerMediaId");

    private static BusinessRuleException CouldNotSend(string word) =>
        new(
            "header_media_upload_failed",
            $"The campaign {word} could not be sent to WhatsApp, so no messages went out. Try starting the campaign again.");

    private static string Describe(MediaKind kind) => kind.ToString().ToLowerInvariant();

    private static string Article(MediaKind kind) => kind == MediaKind.Image ? "an" : "a";

    [LoggerMessage(
        EventId = 2720,
        Level = LogLevel.Warning,
        Message = "Uploading the header media for campaign {CampaignId} to Meta failed; the run is stopped.")]
    private partial void LogUploadFailed(Exception exception, long campaignId);
}
