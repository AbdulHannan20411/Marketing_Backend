using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Applies Meta's delivery receipts and template reviews to stored state.</summary>
public interface IWhatsAppWebhookService
{
    /// <summary>
    /// Applies one webhook payload.
    /// <para>
    /// The caller must have verified the signature first. Nothing in here re-checks it.
    /// </para>
    /// </summary>
    /// <param name="envelope">Parsed payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many receipts were applied.</returns>
    public Task<int> ProcessAsync(WebhookEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppWebhookService" />
public sealed partial class WhatsAppWebhookService : IWhatsAppWebhookService
{
    private readonly ICampaignMessageRepository _messages;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<DeliveryFailure> _failures;
    private readonly IRepository<MessageDailyStat> _stats;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRealtimeNotifier _realtime;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<WhatsAppWebhookService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppWebhookService(
        ICampaignMessageRepository messages,
        IWhatsAppConnectionRepository connections,
        IRepository<Campaign> campaigns,
        IRepository<DeliveryFailure> failures,
        IRepository<MessageDailyStat> stats,
        IRepository<MessageTemplate> templates,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IRealtimeNotifier realtime,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<WhatsAppWebhookService> logger)
    {
        _messages = messages;
        _connections = connections;
        _campaigns = campaigns;
        _failures = failures;
        _stats = stats;
        _templates = templates;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _realtime = realtime;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ProcessAsync(WebhookEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var applied = 0;

        foreach (var change in envelope.Entry?.SelectMany(entry => entry.Changes ?? []) ?? [])
        {
            if (change.Value is not { } value)
            {
                continue;
            }

            var connection = await ResolveConnectionAsync(value.Metadata?.PhoneNumberId, cancellationToken);

            if (connection?.TenantId is not { } tenantId)
            {
                // A receipt for a number this platform does not know about. Signature-valid, so it
                // came from Meta - most likely a number disconnected between send and receipt.
                // Logged and dropped rather than treated as an error Meta should retry.
                LogUnknownNumber(value.Metadata?.PhoneNumberId ?? "(none)");

                continue;
            }

            using (_tenantContext.BeginScope(tenantId))
            {
                applied += await ApplyStatusesAsync(value, connection, cancellationToken);

                await ApplyTemplateReviewAsync(value, cancellationToken);
            }
        }

        return applied;
    }

    /// <summary>Applies every delivery receipt in one change.</summary>
    private async Task<int> ApplyStatusesAsync(
        WebhookValue value,
        WhatsAppConnection connection,
        CancellationToken cancellationToken)
    {
        if (value.Statuses is not { Count: > 0 } statuses)
        {
            return 0;
        }

        var touchedCampaigns = new Dictionary<long, Campaign>();
        var applied = 0;

        foreach (var receipt in statuses)
        {
            if (receipt.MessageId is not { Length: > 0 } messageId
                || !TryParseStatus(receipt.Status, out var status))
            {
                continue;
            }

            var message = await _messages.FindByMetaMessageIdAsync(messageId, cancellationToken);

            if (message is null || !ShouldAdvance(message.Status, status))
            {
                // Meta re-delivers a webhook until it is acknowledged, so the same receipt arrives
                // more than once. Refusing to move backwards is what makes reprocessing harmless -
                // without it, a replayed "delivered" after a "read" would undo the read count.
                continue;
            }

            var campaign = await LoadCampaignAsync(touchedCampaigns, message.CampaignId, cancellationToken);
            var occurredOn = ParseTimestamp(receipt.Timestamp);

            Apply(message, campaign, status, receipt, occurredOn);

            await RecordDailyStatAsync(status, occurredOn, cancellationToken);

            applied++;
        }

        if (applied == 0)
        {
            return 0;
        }

        // Receipts arriving at all is the evidence the subscription works, which is what the
        // connection screen's "webhook healthy" indicator reports.
        connection.WebhookHealthy = true;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var campaign in touchedCampaigns.Values)
        {
            await PublishAsync(campaign, cancellationToken);
        }

        return applied;
    }

    /// <summary>Moves one message and its campaign's counters to the reported status.</summary>
    private void Apply(
        CampaignMessage message,
        Campaign? campaign,
        CampaignMessageStatus status,
        WebhookStatus receipt,
        DateTimeOffset occurredOn)
    {
        message.Status = status;

        switch (status)
        {
            case CampaignMessageStatus.Sent:
                message.SentOn ??= occurredOn;
                break;

            case CampaignMessageStatus.Delivered:
                message.DeliveredOn = occurredOn;

                if (campaign is not null)
                {
                    campaign.DeliveredCount++;
                }

                break;

            case CampaignMessageStatus.Read:
                message.ReadOn = occurredOn;

                if (campaign is not null)
                {
                    campaign.ReadCount++;

                    // Meta reports delivery and read separately and does not guarantee order. A
                    // read message was necessarily delivered, so the delivered counter is corrected
                    // here rather than left understated when the receipts arrive out of sequence.
                    if (message.DeliveredOn is null)
                    {
                        message.DeliveredOn = occurredOn;
                        campaign.DeliveredCount++;
                    }
                }

                break;

            case CampaignMessageStatus.Failed:
                var error = receipt.Errors is { Count: > 0 } errors ? errors[0] : null;

                message.ErrorCode = error?.Code;
                message.ErrorReason = error?.Message ?? error?.Title ?? "Meta reported the message as failed.";

                if (campaign is not null)
                {
                    campaign.FailedCount++;

                    _failures.Add(new DeliveryFailure
                    {
                        TenantId = campaign.TenantId,
                        CampaignId = campaign.Id,
                        CampaignName = campaign.Name,
                        ContactId = message.ContactId,
                        PhoneNumber = message.PhoneNumber,
                        Reason = message.ErrorReason,
                        ErrorCode = message.ErrorCode ?? 0,
                        OccurredOn = occurredOn,
                    });
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Records a template's review outcome against the stored copy.</summary>
    private async Task ApplyTemplateReviewAsync(WebhookValue value, CancellationToken cancellationToken)
    {
        if (value.TemplateEvent is not { Length: > 0 } review
            || value.TemplateName is not { Length: > 0 } name)
        {
            return;
        }

        var template = await _queries.FirstOrDefaultAsync(
            _templates.Query(asNoTracking: false).Where(candidate => candidate.Name == name),
            cancellationToken);

        if (template is null)
        {
            return;
        }

        template.Status = review.ToUpperInvariant() switch
        {
            "APPROVED" => TemplateStatus.Approved,
            "REJECTED" => TemplateStatus.Rejected,
            "PENDING" => TemplateStatus.Pending,
            "PAUSED" or "DISABLED" => TemplateStatus.Paused,
            _ => template.Status,
        };

        template.RejectionReason = template.Status == TemplateStatus.Rejected
            ? value.TemplateRejectionReason
            : null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogTemplateReviewed(name, template.Status);
    }

    /// <summary>Adds one to the pre-aggregated daily counters the dashboard reads.</summary>
    private async Task RecordDailyStatAsync(
        CampaignMessageStatus status,
        DateTimeOffset occurredOn,
        CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(occurredOn.UtcDateTime);

        var stat = await _queries.FirstOrDefaultAsync(
            _stats.Query(asNoTracking: false).Where(candidate => candidate.Date == date),
            cancellationToken);

        if (stat is null)
        {
            stat = new MessageDailyStat
            {
                TenantId = _tenantContext.TenantId,
                Date = date,
            };

            _stats.Add(stat);
        }

        switch (status)
        {
            case CampaignMessageStatus.Sent:
                stat.Sent++;
                break;

            case CampaignMessageStatus.Delivered:
                stat.Delivered++;
                break;

            case CampaignMessageStatus.Read:
                stat.Read++;
                break;

            case CampaignMessageStatus.Failed:
                stat.Failed++;
                break;

            default:
                break;
        }
    }

    /// <summary>Resolves the tenant from the number the receipt was sent from.</summary>
    private async Task<WhatsAppConnection?> ResolveConnectionAsync(
        string? phoneNumberId,
        CancellationToken cancellationToken) =>
        phoneNumberId is { Length: > 0 }
            ? await _connections.FindByPhoneNumberIdAsync(phoneNumberId, cancellationToken)
            : null;

    private async Task<Campaign?> LoadCampaignAsync(
        Dictionary<long, Campaign> cache,
        long campaignId,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(campaignId, out var cached))
        {
            return cached;
        }

        var campaign = await _campaigns.GetForUpdateAsync(campaignId, cancellationToken);

        if (campaign is not null)
        {
            cache[campaignId] = campaign;
        }

        return campaign;
    }

    /// <summary>
    /// Whether a receipt moves a message forward.
    /// <para>
    /// Statuses form a chain - sent, delivered, read - with failure terminal. Ranking them and
    /// refusing to move down the chain makes reprocessing a redelivered webhook a no-op, which is
    /// exactly what "at least once" delivery requires of the receiver.
    /// </para>
    /// </summary>
    private static bool ShouldAdvance(CampaignMessageStatus current, CampaignMessageStatus incoming) =>
        Rank(incoming) > Rank(current);

    private static int Rank(CampaignMessageStatus status) => status switch
    {
        CampaignMessageStatus.Pending => 0,
        CampaignMessageStatus.Sent => 1,
        CampaignMessageStatus.Delivered => 2,
        CampaignMessageStatus.Read => 3,

        // Terminal, and above everything: a message Meta reported as failed did not arrive, and a
        // later receipt must not quietly promote it back into the delivered count.
        CampaignMessageStatus.Failed => 4,
        _ => 0,
    };

    private static bool TryParseStatus(string? value, out CampaignMessageStatus status)
    {
        status = value?.ToUpperInvariant() switch
        {
            "SENT" => CampaignMessageStatus.Sent,
            "DELIVERED" => CampaignMessageStatus.Delivered,
            "READ" => CampaignMessageStatus.Read,
            "FAILED" => CampaignMessageStatus.Failed,
            _ => CampaignMessageStatus.Pending,
        };

        return status != CampaignMessageStatus.Pending;
    }

    /// <summary>Reads Meta's Unix-seconds timestamp, falling back to now when it is absent.</summary>
    private DateTimeOffset ParseTimestamp(string? timestamp) =>
        long.TryParse(timestamp, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : _clock.UtcNow;

    private async Task PublishAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.TenantId is not { } tenantId)
        {
            return;
        }

        await _realtime.PublishCampaignProgressAsync(
            tenantId,
            new CampaignResponse(
                PublicId.From(PublicId.Campaign, campaign.Id),
                campaign.Name,
                campaign.TemplateName,
                campaign.Status,
                new CampaignMetricsResponse(
                    campaign.AudienceSize,
                    campaign.SentCount,
                    campaign.DeliveredCount,
                    campaign.ReadCount,
                    campaign.ClickedCount,
                    campaign.FailedCount),
                campaign.AudienceLabel,
                campaign.ScheduledAt,
                campaign.CompletedAt,
                campaign.CreatedByName,
                campaign.CreatedOn),
            cancellationToken);
    }

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Warning,
        Message = "A webhook arrived for phone number {PhoneNumberId}, which is not connected here.")]
    private partial void LogUnknownNumber(string phoneNumberId);

    [LoggerMessage(
        EventId = 2802,
        Level = LogLevel.Information,
        Message = "Template {TemplateName} was reviewed by Meta: {Status}.")]
    private partial void LogTemplateReviewed(string templateName, TemplateStatus status);
}
