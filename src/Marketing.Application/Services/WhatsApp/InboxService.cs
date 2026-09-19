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
    /// <summary>Lists threads the caller may see, newest activity first.</summary>
    /// <param name="query">Paging, search and filters.</param>
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

    /// <summary>Sends a free-form reply from the number the customer wrote to.</summary>
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

    /// <summary>Assigns a thread to someone who may see its number, or clears the assignment.</summary>
    /// <param name="conversationId">Public conversation identifier.</param>
    /// <param name="request">Who to assign it to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ConversationResponse> AssignAsync(
        string conversationId,
        AssignConversationRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInboxService" />
/// <remarks>
/// The window is the whole feature. Meta allows free-form replies for 24 hours after the customer's
/// last inbound message and nothing but an approved template afterwards, so this service owns the
/// clock: the client blocks a closed window too, but a tab left open for an hour will still try.
/// <para>
/// Per-number access is applied inside every query, not only on writes. An employee without view on
/// a number never receives one of its conversations from a list, a search, a lookup by id or a push.
/// </para>
/// </remarks>
public sealed class InboxService : IInboxService
{
    private const int MaximumPageSize = 100;
    private const int PreviewLength = 300;

    private static readonly ConversationMessageKind[] MediaKinds =
    [
        ConversationMessageKind.Image,
        ConversationMessageKind.Video,
        ConversationMessageKind.Document,
        ConversationMessageKind.Audio,
    ];

    private readonly IRepository<Conversation> _conversations;
    private readonly IRepository<ConversationMessage> _messages;
    private readonly IRepository<MediaAsset> _media;
    private readonly IRepository<User> _users;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWhatsAppGateway _gateway;
    private readonly IMediaService _mediaService;
    private readonly IWhatsAppAccessService _access;
    private readonly IRealtimeNotifier _realtime;
    private readonly ISecretProtector _protector;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public InboxService(
        IRepository<Conversation> conversations,
        IRepository<ConversationMessage> messages,
        IRepository<MediaAsset> media,
        IRepository<User> users,
        IWhatsAppConnectionRepository connections,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IWhatsAppGateway gateway,
        IMediaService mediaService,
        IWhatsAppAccessService access,
        IRealtimeNotifier realtime,
        ISecretProtector protector,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
    {
        _conversations = conversations;
        _messages = messages;
        _media = media;
        _users = users;
        _connections = connections;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _gateway = gateway;
        _mediaService = mediaService;
        _access = access;
        _realtime = realtime;
        _protector = protector;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<PagedResult<ConversationResponse>> SearchAsync(
        ConversationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = await VisibleAsync(cancellationToken);

        if (IsSet(query.AccountId))
        {
            var accountId = PublicId.Parse(PublicId.WhatsAppAccount, query.AccountId, "WhatsApp account");
            var scope = await _access.GetCallerScopeAsync(cancellationToken);

            // A number the caller may not see is a 404, not an empty page that confirms it exists.
            // Checked against access rather than against live numbers, so an administrator can still
            // filter the history of a number that has since been removed.
            if (!scope.Allows(accountId, WhatsAppAccessLevel.View))
            {
                throw new NotFoundException("WhatsApp account", accountId);
            }

            filtered = filtered.Where(conversation => conversation.WhatsAppConnectionId == accountId);
        }

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

        filtered = ApplyStatus(filtered, query.Status);
        filtered = ApplyMessageType(filtered, query.MessageType);
        filtered = ApplyAssignee(filtered, query.AssignedTo);

        var page = await _queries.ToPagedAsync(
            filtered
                // Newest activity first: the thread someone just wrote in is the one being looked for.
                .OrderByDescending(conversation => conversation.LastMessageAt ?? conversation.CreatedOn)
                .Select(conversation => new ConversationRow(
                    conversation.Id,
                    conversation.ContactId,
                    conversation.ContactName,
                    conversation.WaId,
                    conversation.LastMessagePreview,
                    conversation.LastMessageAt,
                    conversation.UnreadCount,
                    conversation.WindowExpiresAt,
                    conversation.WhatsAppConnectionId,
                    conversation.AssignedToUserId,
                    conversation.Messages
                        .OrderByDescending(message => message.OccurredAt)
                        .ThenByDescending(message => message.Id)
                        .Select(message => (MessageDirection?)message.Direction)
                        .FirstOrDefault())),
            query.Page,
            Math.Min(query.PageSize, MaximumPageSize),
            cancellationToken);

        var labels = await _access.LabelsAsync(cancellationToken);
        var assignees = await AssigneeNamesAsync(page.Items.Select(row => row.AssignedToUserId), cancellationToken);
        var now = _clock.UtcNow;

        return page.Map(row => ToResponse(row, labels, assignees, now));
    }

    /// <inheritdoc />
    public async Task<ConversationResponse> GetAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await LoadAsync(conversationId, tracked: false, cancellationToken);

        return await ToResponseAsync(conversation, cancellationToken);
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

        // Always the number the customer wrote to, never the default. A reply from a different
        // number arrives as a message from a stranger, outside the window that number never opened.
        var connection = await _connections.FindForRecordAsync(
            _tenantContext.RequireTenantId(),
            conversation.WhatsAppConnectionId,
            cancellationToken);

        if (connection is not null)
        {
            _access.Demand(
                await _access.GetCallerScopeAsync(cancellationToken),
                connection.Id,
                connection.Label,
                WhatsAppAccessLevel.Reply);
        }

        if (connection is not
            {
                Status: not ConnectionStatus.Disconnected,
                PhoneNumberId: { Length: > 0 } phoneNumberId,
                EncryptedAccessToken: { Length: > 0 } encrypted,
            })
        {
            throw new BusinessRuleException(
                "not_connected",
                connection is null
                    ? "Connect a WhatsApp account before replying."
                    : $"{connection.Label} is disconnected. Reconnect it to reply to this customer.");
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
            connection.LastMessageSentAt = now;
            connection.ApiStatus = "ok";
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

        return await ToResponseAsync(conversation, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConversationResponse> AssignAsync(
        string conversationId,
        AssignConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversation = await LoadAsync(conversationId, tracked: true, cancellationToken);
        var labels = await _access.LabelsAsync(cancellationToken);
        var accountId = conversation.WhatsAppConnectionId ?? 0;
        var label = labels.TryGetValue(accountId, out var known) ? known : "this number";

        // Assigning is part of working the number, so it takes the same right as replying on it.
        _access.Demand(await _access.GetCallerScopeAsync(cancellationToken), accountId, label, WhatsAppAccessLevel.Reply);

        long? assigneeId = null;

        if (request.UserId is { Length: > 0 } userId)
        {
            // emp_ is what every employee screen shows; usr_ is accepted too, since the contract was
            // written with it.
            if (!PublicId.TryParse(PublicId.Employee, userId, out var parsed)
                && !PublicId.TryParse("usr", userId, out parsed))
            {
                throw new ValidationException("userId", "Choose a member of this workspace.");
            }

            var assignee = await _queries.FirstOrDefaultAsync(
                _users.Query()
                    .Where(user => user.Id == parsed && user.Status == Common.Constants.AppConstants.UserStatus.Active)
                    .Select(user => new { user.Id, user.DisplayName }),
                cancellationToken)
                ?? throw new ValidationException("userId", "Choose an active member of this workspace.");

            var scope = await _access.GetScopeForUserAsync(assignee.Id, cancellationToken);

            if (!scope.Allows(accountId, WhatsAppAccessLevel.View))
            {
                throw new ValidationException("userId", $"{assignee.DisplayName} cannot see the {label} number.");
            }

            assigneeId = assignee.Id;
        }

        if (conversation.AssignedToUserId != assigneeId)
        {
            conversation.AssignedToUserId = assigneeId;

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var response = await ToResponseAsync(conversation, cancellationToken);

        if (conversation.WhatsAppConnectionId is { } connectionId)
        {
            // To the people who may see the thread, so another agent's list moves it out of
            // "unassigned" without a refresh - and to nobody who may not.
            await _realtime.PublishConversationAssignedAsync(
                await _access.UsersWhoMayViewAsync(connectionId, cancellationToken),
                response,
                cancellationToken);
        }

        return response;
    }

    /// <summary>The conversations the caller may see at all.</summary>
    private async Task<IQueryable<Conversation>> VisibleAsync(CancellationToken cancellationToken)
    {
        var scope = await _access.GetCallerScopeAsync(cancellationToken);

        if (scope.IsUnrestricted)
        {
            return _conversations.Query();
        }

        var viewable = scope.ViewableAccountIds;

        return _conversations.Query().Where(conversation =>
            conversation.WhatsAppConnectionId != null && viewable.Contains(conversation.WhatsAppConnectionId.Value));
    }

    /// <summary>Loads a thread, or reports that the caller has no such thread.</summary>
    /// <remarks>
    /// A thread on a number the caller may not see is not found, exactly like one in another
    /// workspace, so its existence cannot be probed by id.
    /// </remarks>
    private async Task<Conversation> LoadAsync(string conversationId, bool tracked, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Conversation, conversationId, "conversation");

        var conversation = await _queries.FirstOrDefaultAsync(
            _conversations.Query(asNoTracking: !tracked).Where(conversation => conversation.Id == id),
            cancellationToken);

        var scope = await _access.GetCallerScopeAsync(cancellationToken);

        if (conversation is null
            || (!scope.IsUnrestricted
                && (conversation.WhatsAppConnectionId is not { } accountId
                    || !scope.Allows(accountId, WhatsAppAccessLevel.View))))
        {
            throw new NotFoundException("Conversation", conversationId);
        }

        return conversation;
    }

    private static bool IsSet(string? filter) =>
        !string.IsNullOrWhiteSpace(filter)
        && !string.Equals(filter, ConversationQuery.All, StringComparison.OrdinalIgnoreCase);

    private static IQueryable<Conversation> ApplyStatus(IQueryable<Conversation> filtered, string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            null or "" or ConversationQuery.All => filtered,
            "unread" => filtered.Where(conversation => conversation.UnreadCount > 0),

            // By the last message, not by the unread count: a thread someone opened and walked away
            // from is still waiting for an answer.
            "awaiting_reply" => filtered.Where(conversation =>
                conversation.Messages
                    .OrderByDescending(message => message.OccurredAt)
                    .ThenByDescending(message => message.Id)
                    .Select(message => (MessageDirection?)message.Direction)
                    .FirstOrDefault() == MessageDirection.Inbound),
            "replied" => filtered.Where(conversation =>
                conversation.Messages
                    .OrderByDescending(message => message.OccurredAt)
                    .ThenByDescending(message => message.Id)
                    .Select(message => (MessageDirection?)message.Direction)
                    .FirstOrDefault() == MessageDirection.Outbound),
            _ => throw new ValidationException("status", "Status must be all, unread, awaiting_reply or replied."),
        };

    private static IQueryable<Conversation> ApplyMessageType(IQueryable<Conversation> filtered, string? type)
    {
        ConversationMessageKind[]? kinds = type?.Trim().ToLowerInvariant() switch
        {
            null or "" or ConversationQuery.All => null,
            "text" => [ConversationMessageKind.Text],
            "media" => MediaKinds,
            "template" => [ConversationMessageKind.Template],
            _ => throw new ValidationException("messageType", "Message type must be all, text, media or template."),
        };

        return kinds is null
            ? filtered
            : filtered.Where(conversation => conversation.Messages.Any() && kinds.Contains(
                conversation.Messages
                    .OrderByDescending(message => message.OccurredAt)
                    .ThenByDescending(message => message.Id)
                    .Select(message => message.Kind)
                    .FirstOrDefault()));
    }

    private IQueryable<Conversation> ApplyAssignee(IQueryable<Conversation> filtered, string? assignedTo)
    {
        var value = assignedTo?.Trim();

        if (!IsSet(value))
        {
            return filtered;
        }

        if (string.Equals(value, "unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return filtered.Where(conversation => conversation.AssignedToUserId == null);
        }

        long userId;

        if (string.Equals(value, "me", StringComparison.OrdinalIgnoreCase))
        {
            userId = _currentUser.UserId ?? 0;
        }
        else if (!PublicId.TryParse(PublicId.Employee, value, out userId) && !PublicId.TryParse("usr", value, out userId))
        {
            throw new ValidationException("assignedTo", "Assigned to must be all, me, unassigned or an employee id.");
        }

        return filtered.Where(conversation => conversation.AssignedToUserId == userId);
    }

    /// <summary>Display names for the people threads are assigned to, in one query.</summary>
    private async Task<IReadOnlyDictionary<long, string>> AssigneeNamesAsync(
        IEnumerable<long?> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.OfType<long>().Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        // Deleted people included: a thread assigned to someone who has left still says who.
        var rows = await _queries.ToListAsync(
            _users.Query().IgnoreQueryFilters()
                .Where(user => ids.Contains(user.Id) && user.TenantId == _tenantContext.TenantId)
                .Select(user => new { user.Id, user.DisplayName }),
            cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.DisplayName);
    }

    /// <summary>Resolves the list-shaped response for one loaded thread.</summary>
    private async Task<ConversationResponse> ToResponseAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        var lastDirection = await _queries.FirstOrDefaultAsync(
            _messages.Query()
                .Where(message => message.ConversationId == conversation.Id)
                .OrderByDescending(message => message.OccurredAt)
                .ThenByDescending(message => message.Id)
                .Select(message => (MessageDirection?)message.Direction),
            cancellationToken);

        return ToResponse(
            new ConversationRow(
                conversation.Id,
                conversation.ContactId,
                conversation.ContactName,
                conversation.WaId,
                conversation.LastMessagePreview,
                conversation.LastMessageAt,
                conversation.UnreadCount,
                conversation.WindowExpiresAt,
                conversation.WhatsAppConnectionId,
                conversation.AssignedToUserId,
                lastDirection),
            await _access.LabelsAsync(cancellationToken),
            await AssigneeNamesAsync([conversation.AssignedToUserId], cancellationToken),
            _clock.UtcNow);
    }

    private static ConversationResponse ToResponse(
        ConversationRow row,
        IReadOnlyDictionary<long, string> labels,
        IReadOnlyDictionary<long, string> assignees,
        DateTimeOffset now) =>
        new(
            PublicId.From(PublicId.Conversation, row.Id),
            PublicId.FromNullable(PublicId.Contact, row.ContactId),
            row.ContactName,
            PhoneNumbers.ToDisplayForm(row.WaId),
            row.LastMessagePreview,
            row.LastMessageAt,
            row.UnreadCount,
            Open(row.WindowExpiresAt, now),
            PublicId.FromNullable(PublicId.WhatsAppAccount, row.AccountId),
            row.AccountId is { } accountId && labels.TryGetValue(accountId, out var label) ? label : null,
            row.LastDirection == MessageDirection.Inbound,
            row.AssignedToUserId is { } assigneeId && assignees.TryGetValue(assigneeId, out var name)
                ? new ConversationAssigneeResponse(PublicId.From(PublicId.Employee, assigneeId), name)
                : null);

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

    /// <summary>A thread as the list needs it, with the direction of its last message.</summary>
    private sealed record ConversationRow(
        long Id,
        long? ContactId,
        string ContactName,
        string WaId,
        string LastMessagePreview,
        DateTimeOffset? LastMessageAt,
        int UnreadCount,
        DateTimeOffset? WindowExpiresAt,
        long? AccountId,
        long? AssignedToUserId,
        MessageDirection? LastDirection);
}
