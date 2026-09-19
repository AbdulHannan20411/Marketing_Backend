using System.Globalization;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>Turns the messages a webhook carries into conversations an agent can answer.</summary>
public interface IInboundMessageService
{
    /// <summary>Applies every inbound message in one webhook change.</summary>
    /// <param name="value">The change Meta sent.</param>
    /// <param name="connection">Connection the change belongs to, already resolved to a tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many messages were stored.</returns>
    public Task<int> ApplyAsync(
        WebhookValue value,
        WhatsAppConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an agent's reply to the state Meta reports, so the thread shows delivery ticks.
    /// </summary>
    /// <param name="metaMessageId">Meta's message identifier from the receipt.</param>
    /// <param name="status">Status Meta reported.</param>
    /// <param name="failureReason">Why it failed, when it did.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether a reply was found and moved.</returns>
    public Task<bool> ApplyReceiptAsync(
        string metaMessageId,
        string? status,
        string? failureReason,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInboundMessageService" />
/// <remarks>
/// Resetting the window is the single most important thing this class does. Everything else - the
/// unread count, the preview, the attachment - is presentation; the window is what decides whether
/// an agent may answer at all for the next 24 hours.
/// </remarks>
public sealed partial class InboundMessageService : IInboundMessageService
{
    /// <summary>How long a customer's message lets the business reply freely.</summary>
    private const int WindowHours = 24;

    private const int PreviewLength = 300;

    private readonly IRepository<Conversation> _conversations;
    private readonly IRepository<ConversationMessage> _messages;
    private readonly IRepository<Contact> _contacts;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMediaService _media;
    private readonly ISecretProtector _protector;
    private readonly IRealtimeNotifier _realtime;
    private readonly IDateTimeProvider _clock;
    private readonly IWhatsAppAccessService _access;
    private readonly ILogger<InboundMessageService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public InboundMessageService(
        IRepository<Conversation> conversations,
        IRepository<ConversationMessage> messages,
        IRepository<Contact> contacts,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IMediaService media,
        ISecretProtector protector,
        IRealtimeNotifier realtime,
        IDateTimeProvider clock,
        IWhatsAppAccessService access,
        ILogger<InboundMessageService> logger)
    {
        _access = access;
        _conversations = conversations;
        _messages = messages;
        _contacts = contacts;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _media = media;
        _protector = protector;
        _realtime = realtime;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ApplyAsync(
        WebhookValue value,
        WhatsAppConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(connection);

        if (value.Messages is not { Count: > 0 } messages || connection.TenantId is not { } tenantId)
        {
            return 0;
        }

        var accessToken = connection.EncryptedAccessToken is { Length: > 0 } encrypted
            ? _protector.Unprotect(encrypted)
            : null;

        var applied = 0;

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (message.Id is not { Length: > 0 } metaMessageId || message.From is not { Length: > 0 } from)
            {
                continue;
            }

            // Meta redelivers, so an id already stored is not an error - it is the same message
            // arriving twice, and the second copy must change nothing.
            if (await ExistsAsync(metaMessageId, cancellationToken))
            {
                continue;
            }

            try
            {
                await StoreAsync(value, message, metaMessageId, from, connection, tenantId, accessToken, cancellationToken);

                applied++;
            }
            catch (DbUpdateException exception)
            {
                // Two deliveries of the same message can race past the check above; the unique index
                // is what actually settles it. Losing the rest of the batch over that would be worse
                // than the duplicate it prevented.
                LogDuplicateInbound(exception, metaMessageId);
            }
        }

        return applied;
    }

    /// <inheritdoc />
    public async Task<bool> ApplyReceiptAsync(
        string metaMessageId,
        string? status,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        if (metaMessageId is not { Length: > 0 } || ParseStatus(status) is not { } reported)
        {
            return false;
        }

        var message = await _queries.FirstOrDefaultAsync(
            _messages.Query(asNoTracking: false)
                .Where(candidate =>
                    candidate.MetaMessageId == metaMessageId
                    && candidate.Direction == MessageDirection.Outbound),
            cancellationToken);

        // Never backwards. Meta redelivers out of order, and a late "sent" arriving after "read"
        // would make the thread appear to regress in front of the agent watching it.
        if (message is null || Rank(reported) <= Rank(message.Status))
        {
            return false;
        }

        message.Status = reported;

        if (reported == InboxMessageStatus.Failed && failureReason is { Length: > 0 })
        {
            message.FailureReason = failureReason;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Meta's status words, as this platform records them.</summary>
    private static InboxMessageStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "sent" => InboxMessageStatus.Sent,
        "delivered" => InboxMessageStatus.Delivered,
        "read" => InboxMessageStatus.Read,
        "failed" => InboxMessageStatus.Failed,
        _ => null,
    };

    /// <summary>How far along a status is, so a late one cannot undo a later one.</summary>
    private static int Rank(InboxMessageStatus status) => status switch
    {
        InboxMessageStatus.Queued => 0,
        InboxMessageStatus.Sent => 1,
        InboxMessageStatus.Delivered => 2,
        InboxMessageStatus.Read => 3,

        // Terminal: a failure is never overtaken by a receipt that arrives afterwards.
        InboxMessageStatus.Failed => 4,
        _ => 0,
    };

    /// <summary>Writes one inbound message, its conversation and its attachment.</summary>
    private async Task StoreAsync(
        WebhookValue value,
        WebhookInboundMessage message,
        string metaMessageId,
        string from,
        WhatsAppConnection connection,
        long tenantId,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        var occurredAt = ParseTimestamp(message.Timestamp) ?? _clock.UtcNow;
        var conversation = await FindOrCreateConversationAsync(
            from, ProfileName(value, from), connection.Id, tenantId, cancellationToken);
        var described = Describe(message);

        MediaAsset? media = null;

        if (described.Media is { } reference && accessToken is { Length: > 0 })
        {
            // Downloaded now rather than on demand: Meta's copy expires after 30 days, and a thread
            // read six weeks later would otherwise show a broken attachment.
            media = await _media.StoreInboundAsync(reference.Id, reference.FileName, accessToken, cancellationToken);
        }

        _messages.Add(new ConversationMessage
        {
            TenantId = tenantId,
            ConversationId = conversation.Id,
            MetaMessageId = metaMessageId,
            Direction = MessageDirection.Inbound,
            Kind = described.Kind,
            Body = described.Body,
            MediaId = media?.Id,

            // Delivered by definition: it is in hand. Inbound messages have no further states to
            // travel through, and Meta sends no receipts for them.
            Status = InboxMessageStatus.Delivered,
            OccurredAt = occurredAt,
        });

        // The line the whole inbox depends on.
        conversation.WindowExpiresAt = occurredAt.AddHours(WindowHours);
        conversation.UnreadCount += 1;
        conversation.LastMessagePreview = Preview(described);
        conversation.LastMessageAt = occurredAt;

        // Health: a customer can reach this number.
        if (connection.LastMessageReceivedAt is null || connection.LastMessageReceivedAt < occurredAt)
        {
            connection.LastMessageReceivedAt = occurredAt;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await PublishAsync(connection, conversation, described, occurredAt, cancellationToken);
    }

    /// <summary>The thread between one customer and one of the workspace's numbers, created on first contact.</summary>
    /// <remarks>
    /// Per number, not per customer. A customer who writes to Sales and to Support has two
    /// conversations, each with its own 24-hour window - which is how Meta counts them too - and each
    /// visible only to the people who may see that number.
    /// </remarks>
    private async Task<Conversation> FindOrCreateConversationAsync(
        string waId,
        string? profileName,
        long connectionId,
        long tenantId,
        CancellationToken cancellationToken)
    {
        var normalised = PhoneNumbers.Normalise(waId);

        var existing = await _queries.FirstOrDefaultAsync(
            _conversations.Query(asNoTracking: false).Where(conversation =>
                conversation.WaId == normalised && conversation.WhatsAppConnectionId == connectionId),
            cancellationToken);

        if (existing is not null)
        {
            // A saved contact's name wins; Meta's profile name fills the gap until someone saves them.
            if (existing.ContactName.Length == 0 && profileName is { Length: > 0 })
            {
                existing.ContactName = profileName;
            }

            return existing;
        }

        // Matched, never created. A number that wrote in is not automatically a contact: importing
        // strangers into a customer list is the tenant's decision, not this platform's.
        var contact = await _queries.FirstOrDefaultAsync(
            _contacts.Query().Where(candidate => candidate.NormalizedPhoneNumber == normalised),
            cancellationToken);

        var conversation = new Conversation
        {
            TenantId = tenantId,
            WhatsAppConnectionId = connectionId,
            WaId = normalised,
            ContactId = contact?.Id,
            ContactName = contact?.FullName ?? profileName ?? PhoneNumbers.ToDisplayForm(normalised),
        };

        _conversations.Add(conversation);

        return conversation;
    }

    private async Task<bool> ExistsAsync(string metaMessageId, CancellationToken cancellationToken) =>
        await _queries.FirstOrDefaultAsync(
            _messages.Query().Where(message => message.MetaMessageId == metaMessageId).Select(message => message.Id),
            cancellationToken) != 0;

    /// <summary>Tells the open inbox that something arrived, so it need not poll.</summary>
    private async Task PublishAsync(
        WhatsAppConnection connection,
        Conversation conversation,
        DescribedMessage described,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            // To each person who may read this number, never to the workspace as a whole: an
            // employee without access to it must not receive its customers' messages by push.
            await _realtime.PublishInboundMessageAsync(
                await _access.UsersWhoMayViewAsync(connection.Id, cancellationToken),
                new InboundMessageEvent(
                    PublicId.From(PublicId.Conversation, conversation.Id),
                    conversation.ContactName,
                    PhoneNumbers.ToDisplayForm(conversation.WaId),
                    Preview(described),
                    conversation.UnreadCount,
                    conversation.WindowExpiresAt,
                    occurredAt,
                    PublicId.From(PublicId.WhatsAppAccount, connection.Id),
                    connection.Label),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is already stored. A realtime hiccup costs a refresh, not a message.
            LogPublishFailed(exception, conversation.Id);
        }
    }

    /// <summary>What a message is, in this platform's terms.</summary>
    private static DescribedMessage Describe(WebhookInboundMessage message) =>
        message.Type?.ToLowerInvariant() switch
        {
            "text" => new DescribedMessage(ConversationMessageKind.Text, message.Text?.Body ?? string.Empty, null),
            "image" => Media(ConversationMessageKind.Image, message.Image),
            "video" => Media(ConversationMessageKind.Video, message.Video),
            "document" => Media(ConversationMessageKind.Document, message.Document),
            "audio" => Media(ConversationMessageKind.Audio, message.Audio),

            // A tapped button or list item reads as what the customer chose, because that is what an
            // agent needs to see in the thread.
            "button" => new DescribedMessage(
                ConversationMessageKind.Text,
                message.Button?.Text ?? message.Button?.Payload ?? string.Empty,
                null),
            "interactive" => new DescribedMessage(
                ConversationMessageKind.Text,
                message.Interactive?.ButtonReply?.Title ?? message.Interactive?.ListReply?.Title ?? string.Empty,
                null),

            // Everything else is described rather than dropped: a thread with "Location shared" is
            // honest, a thread with a silent gap is not.
            "location" => new DescribedMessage(ConversationMessageKind.System, "Shared a location", null),
            "contacts" => new DescribedMessage(ConversationMessageKind.System, "Shared a contact", null),
            "sticker" => new DescribedMessage(ConversationMessageKind.System, "Sent a sticker", null),
            "reaction" => new DescribedMessage(ConversationMessageKind.System, "Reacted to a message", null),
            _ => new DescribedMessage(ConversationMessageKind.System, "Sent a message this app cannot show", null),
        };

    private static DescribedMessage Media(ConversationMessageKind kind, WebhookMediaPayload? payload) =>
        new(
            kind,
            payload?.Caption ?? string.Empty,
            payload?.Id is { Length: > 0 } id ? new MediaReference(id, payload.FileName) : null);

    /// <summary>Unix seconds, as Meta sends them.</summary>
    private static DateTimeOffset? ParseTimestamp(string? timestamp) =>
        long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static string? ProfileName(WebhookValue value, string waId) =>
        value.Contacts?.FirstOrDefault(contact =>
            string.Equals(contact.WaId, waId, StringComparison.Ordinal))?.Profile?.Name;

    private static string Preview(DescribedMessage described)
    {
        var text = described.Body.Trim();

        if (text.Length > 0)
        {
            return text.Length <= PreviewLength ? text : text[..PreviewLength];
        }

        return described.Kind switch
        {
            ConversationMessageKind.Image => "Photo",
            ConversationMessageKind.Video => "Video",
            ConversationMessageKind.Document => "Document",
            ConversationMessageKind.Audio => "Voice message",
            _ => "Message",
        };
    }

    /// <summary>A message reduced to what this platform stores.</summary>
    private sealed record DescribedMessage(ConversationMessageKind Kind, string Body, MediaReference? Media);

    /// <summary>An attachment Meta is holding, before it is downloaded.</summary>
    private sealed record MediaReference(string Id, string? FileName);

    [LoggerMessage(
        EventId = 2740,
        Level = LogLevel.Information,
        Message = "Inbound message {MetaMessageId} was already stored; the redelivery changed nothing.")]
    private partial void LogDuplicateInbound(Exception exception, string metaMessageId);

    [LoggerMessage(
        EventId = 2741,
        Level = LogLevel.Warning,
        Message = "Could not announce an inbound message on conversation {ConversationId}; the inbox will show it on refresh.")]
    private partial void LogPublishFailed(Exception exception, long conversationId);
}
