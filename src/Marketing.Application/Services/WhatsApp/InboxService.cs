using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>The shared inbox: customer threads, and replies inside Meta's 24-hour window.</summary>
public interface IInboxService
{
    /// <summary>Lists threads, newest activity first.</summary>
    /// <param name="query">Paging and search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ConversationResponse>> SearchAsync(
        ConversationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one thread.</summary>
    /// <param name="conversationId">Public conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ConversationResponse> GetAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Reads a thread's messages, oldest first.</summary>
    /// <param name="conversationId">Public conversation identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ConversationMessageResponse>> GetMessagesAsync(
        string conversationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a free-form reply, which Meta allows only inside the window.</summary>
    /// <param name="conversationId">Public conversation identifier.</param>
    /// <param name="request">What to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ConversationMessageResponse> SendAsync(
        string conversationId,
        SendConversationMessageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the unread count. Idempotent.</summary>
    /// <param name="conversationId">Public conversation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ConversationResponse> MarkReadAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInboxService" />
/// <remarks>
/// The window is the whole feature. Meta allows free-form replies for 24 hours after the customer's
/// last inbound message and nothing but an approved template afterwards, so this service owns the
/// clock: the client blocks a closed window too, but a tab left open for an hour will still try.
/// </remarks>
public sealed class InboxService : IInboxService
{
    private const int MaximumPageSize = 100;
    private const int PreviewLength = 300;

    private readonly IRepository<Conversation> _conversations;
    private readonly IRepository<ConversationMessage> _messages;
    private readonly IRepository<MediaAsset> _media;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWhatsAppGateway _gateway;
    private readonly IMediaService _mediaService;
    private readonly ISecretProtector _protector;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public InboxService(
        IRepository<Conversation> conversations,
        IRepository<ConversationMessage> messages,
        IRepository<MediaAsset> media,
        IWhatsAppConnectionRepository connections,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IWhatsAppGateway gateway,
        IMediaService mediaService,
        ISecretProtector protector,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
    {
        _conversations = conversations;
        _messages = messages;
        _media = media;
        _connections = connections;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _gateway = gateway;
        _mediaService = mediaService;
        _protector = protector;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<PagedResult<ConversationResponse>> SearchAsync(
        ConversationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = _conversations.Query();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var digits = PhoneNumbers.Normalise(term);

            // Name or number. A search box above a list of people is used for both, and a number
            // typed with spaces or a plus has to match the digits stored against the thread.
            filtered = filtered.Where(conversation =>
                EF.Functions.ILike(conversation.ContactName, $"%{term}%")
                || EF.Functions.ILike(conversation.WaId, $"%{term}%")
                || (digits.Length > 0 && EF.Functions.ILike(conversation.WaId, $"%{digits}%")));
        }

        var page = await _queries.ToPagedAsync(
            filtered
                // Newest activity first: the thread someone just wrote in is the one being looked for.
                .OrderByDescending(conversation => conversation.LastMessageAt ?? conversation.CreatedOn)
                .Select(conversation => new
                {
                    conversation.Id,
                    conversation.ContactId,
                    conversation.ContactName,
                    conversation.WaId,
                    conversation.LastMessagePreview,
                    conversation.LastMessageAt,
                    conversation.UnreadCount,
                    conversation.WindowExpiresAt,
                }),
            query.Page,
            Math.Min(query.PageSize, MaximumPageSize),
            cancellationToken);

        var now = _clock.UtcNow;

        return page.Map(row => new ConversationResponse(
            PublicId.From(PublicId.Conversation, row.Id),
            PublicId.FromNullable(PublicId.Contact, row.ContactId),
            row.ContactName,
            PhoneNumbers.ToDisplayForm(row.WaId),
            row.LastMessagePreview,
            row.LastMessageAt,
            row.UnreadCount,
            Open(row.WindowExpiresAt, now)));
    }

    /// <inheritdoc />
    public async Task<ConversationResponse> GetAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await LoadAsync(conversationId, tracked: false, cancellationToken);

        return ToResponse(conversation);
    }

    /// <inheritdoc />
    public async Task<PagedResult<ConversationMessageResponse>> GetMessagesAsync(
        string conversationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var conversation = await LoadAsync(conversationId, tracked: false, cancellationToken);

        var messages = await _queries.ToPagedAsync(
            _messages.Query()
                .Where(message => message.ConversationId == conversation.Id)
                // Oldest first, so the thread reads downwards the way a chat does.
                .OrderBy(message => message.OccurredAt)
                .ThenBy(message => message.Id)
                .Include(message => message.Media),
            page,
            Math.Min(pageSize, MaximumPageSize),
            cancellationToken);

        return messages.Map(ToResponse);
    }

    /// <inheritdoc />
    public async Task<ConversationMessageResponse> SendAsync(
        string conversationId,
        SendConversationMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversation = await LoadAsync(conversationId, tracked: true, cancellationToken);
        var connection = await _connections.FindForTenantAsync(_tenantContext.RequireTenantId(), cancellationToken);

        if (connection is not
            {
                PhoneNumberId: { Length: > 0 } phoneNumberId,
                EncryptedAccessToken: { Length: > 0 } encrypted,
            })
        {
            throw new BusinessRuleException(
                "not_connected",
                "Connect a WhatsApp account before replying.");
        }

        var now = _clock.UtcNow;

        // The server owns the clock. Meta's own error for a closed window is opaque, and this check
        // costs nothing next to a refused send.
        if (conversation.WindowExpiresAt is not { } expiry || expiry <= now)
        {
            throw new BusinessRuleException(
                "window_closed",
                "This customer last wrote more than 24 hours ago, so only an approved template can be sent.");
        }

        if (request.Kind is ConversationMessageKind.Template or ConversationMessageKind.System)
        {
            throw new ValidationException("kind", "Send a template through a campaign, not as a reply.");
        }

        var body = request.Body?.Trim() ?? string.Empty;
        var media = await LoadMediaAsync(request, cancellationToken);

        if (request.Kind == ConversationMessageKind.Text && body.Length == 0)
        {
            throw new ValidationException("body", "Write a message before sending.");
        }

        var message = new ConversationMessage
        {
            TenantId = conversation.TenantId,
            ConversationId = conversation.Id,
            Direction = MessageDirection.Outbound,
            Kind = request.Kind,
            Body = body,
            MediaId = media?.Id,
            Status = InboxMessageStatus.Queued,
            OccurredAt = now,
        };

        // Written before the call to Meta. A reply that vanishes because the process died mid-send is
        // one an agent types again, and the customer receives twice.
        _messages.Add(message);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var accessToken = _protector.Unprotect(encrypted);

        try
        {
            message.MetaMessageId = request.Kind == ConversationMessageKind.Text
                ? await _gateway.SendTextAsync(phoneNumberId, conversation.WaId, body, accessToken, cancellationToken)
                : await _gateway.SendMediaAsync(
                    phoneNumberId,
                    conversation.WaId,
                    request.Kind,
                    media!.MetaMediaId!,
                    body,
                    accessToken,
                    cancellationToken);

            message.Status = InboxMessageStatus.Sent;
        }
        catch (ExternalServiceException exception)
        {
            // Kept, and shown as failed. The agent needs to know the customer never got it, which a
            // disappearing bubble does not tell them.
            message.Status = InboxMessageStatus.Failed;
            message.FailureReason = MetaSendErrors.Describe(exception.ProviderErrorCode)?.Reason ?? exception.Message;
        }

        conversation.LastMessagePreview = Preview(message);
        conversation.LastMessageAt = now;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        message.Media = media;

        return ToResponse(message);
    }

    /// <inheritdoc />
    public async Task<ConversationResponse> MarkReadAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await LoadAsync(conversationId, tracked: true, cancellationToken);

        if (conversation.UnreadCount != 0)
        {
            conversation.UnreadCount = 0;

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ToResponse(conversation);
    }

    /// <summary>Loads a thread, or reports that this workspace has no such thread.</summary>
    private async Task<Conversation> LoadAsync(string conversationId, bool tracked, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Conversation, conversationId, "conversation");

        return await _queries.FirstOrDefaultAsync(
            _conversations.Query(asNoTracking: !tracked).Where(conversation => conversation.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Conversation", conversationId);
    }

    /// <summary>Resolves the attachment a non-text reply must carry.</summary>
    private async Task<MediaAsset?> LoadMediaAsync(
        SendConversationMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind == ConversationMessageKind.Text)
        {
            return null;
        }

        if (request.MediaId is not { Length: > 0 } mediaId)
        {
            throw new ValidationException("mediaId", "Attach a file before sending.");
        }

        var id = PublicId.Parse(PublicId.Media, mediaId, "media");

        var media = await _queries.FirstOrDefaultAsync(
            _media.Query().Where(asset => asset.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Media", mediaId);

        // Meta sends by its own handle, not by ours. A file this platform stored but Meta refused has
        // no handle, and sending it would fail with an error nobody could act on.
        if (media.MetaMediaId is not { Length: > 0 })
        {
            throw new BusinessRuleException(
                "media_not_uploaded",
                "That file has not reached WhatsApp yet. Upload it again before sending.");
        }

        return media;
    }

    /// <summary>The window, reported only while it is still open.</summary>
    private static DateTimeOffset? Open(DateTimeOffset? windowExpiresAt, DateTimeOffset now) =>
        windowExpiresAt is { } expiry && expiry > now ? expiry : null;

    private ConversationResponse ToResponse(Conversation conversation) =>
        new(
            PublicId.From(PublicId.Conversation, conversation.Id),
            PublicId.FromNullable(PublicId.Contact, conversation.ContactId),
            conversation.ContactName,
            PhoneNumbers.ToDisplayForm(conversation.WaId),
            conversation.LastMessagePreview,
            conversation.LastMessageAt,
            conversation.UnreadCount,
            Open(conversation.WindowExpiresAt, _clock.UtcNow));

    private ConversationMessageResponse ToResponse(ConversationMessage message) =>
        new(
            PublicId.From(PublicId.Message, message.Id),
            message.Direction,
            message.Kind,
            message.Body,
            message.Media is { } media ? _mediaService.ToResponse(media) : null,
            message.Status,
            message.FailureReason,
            message.TemplateName,
            message.OccurredAt);

    /// <summary>One line describing a message, for the conversation list.</summary>
    private static string Preview(ConversationMessage message)
    {
        var text = message.Body?.Trim() ?? string.Empty;

        if (text.Length > 0)
        {
            return text.Length <= PreviewLength ? text : text[..PreviewLength];
        }

        return message.Kind switch
        {
            ConversationMessageKind.Image => "Photo",
            ConversationMessageKind.Video => "Video",
            ConversationMessageKind.Document => "Document",
            ConversationMessageKind.Audio => "Voice message",
            ConversationMessageKind.Template => message.TemplateName ?? "Template",
            _ => "Message",
        };
    }
}
