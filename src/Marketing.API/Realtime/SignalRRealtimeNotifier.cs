using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Realtime;

/// <summary>SignalR implementation of <see cref="IRealtimeNotifier"/>.</summary>
public sealed partial class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IRealtimeDispatcher _queue;
    private readonly ILogger<SignalRRealtimeNotifier> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="queue">Where pushes are left for the background sender.</param>
    /// <param name="logger">Logger.</param>
    public SignalRRealtimeNotifier(IRealtimeDispatcher queue, ILogger<SignalRRealtimeNotifier> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task NotifyUserAsync(
        long userId,
        AppNotification notification,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            RealtimeHub.UserGroup(userId),
            RealtimeEvents.NotificationReceived,
            notification,
            cancellationToken);

    /// <inheritdoc />
    public Task NotifyTenantAsync(
        long tenantId,
        AppNotification notification,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            RealtimeHub.TenantGroup(tenantId),
            RealtimeEvents.NotificationReceived,
            notification,
            cancellationToken);

    /// <inheritdoc />
    public Task PublishCampaignProgressAsync(
        long tenantId,
        CampaignResponse campaign,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            RealtimeHub.TenantGroup(tenantId),
            RealtimeEvents.CampaignProgress,
            campaign,
            cancellationToken);

    /// <inheritdoc />
    /// <inheritdoc />
    public Task PublishInboundMessageAsync(
        IReadOnlyCollection<long> userIds,
        InboundMessageEvent message,
        CancellationToken cancellationToken = default) =>
        SendToUsersAsync(userIds, RealtimeEvents.InboundMessage, message, cancellationToken);

    /// <inheritdoc />
    public Task PublishConversationAssignedAsync(
        IReadOnlyCollection<long> userIds,
        Application.DTOs.WhatsApp.ConversationResponse conversation,
        CancellationToken cancellationToken = default) =>
        SendToUsersAsync(userIds, RealtimeEvents.ConversationAssigned, conversation, cancellationToken);

    public Task PublishImportProgressAsync(
        long tenantId,
        ImportProgress progress,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            RealtimeHub.TenantGroup(tenantId),
            RealtimeEvents.ImportProgress,
            progress,
            cancellationToken);

    /// <inheritdoc />
    public async Task PublishPaymentRequestAsync(
        long? tenantId,
        PaymentRequestEvent payment,
        CancellationToken cancellationToken = default)
    {
        // Reviewers always, because the queue is theirs to work.
        await SendAsync(RealtimeHub.PlatformGroup(), RealtimeEvents.PaymentRequestUpdated, payment, cancellationToken);

        if (tenantId is { } id)
        {
            await SendAsync(
                RealtimeHub.TenantGroup(id), RealtimeEvents.PaymentRequestUpdated, payment, cancellationToken);
        }
    }

    /// <summary>
    /// Sends to a group, swallowing transport failures.
    /// <para>
    /// Best-effort on purpose. The data behind a push is already committed, so a failed send must
    /// not surface as a failed request - the client picks it up on its next fetch. Letting a
    /// dropped WebSocket roll back a successful campaign send would be absurd.
    /// </para>
    /// </summary>
    /// <summary>Queues a push to each person's own group, and to nobody else.</summary>
    private Task SendToUsersAsync<TPayload>(
        IReadOnlyCollection<long> userIds,
        string method,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0 || payload is null)
        {
            return Task.CompletedTask;
        }

        return SendAsync([.. userIds.Select(RealtimeHub.UserGroup)], method, payload, cancellationToken);
    }

    private Task SendAsync<TPayload>(
        string group,
        string method,
        TPayload payload,
        CancellationToken cancellationToken) =>
        payload is null ? Task.CompletedTask : SendAsync([group], method, payload, cancellationToken);

    /// <summary>
    /// Leaves the push for the background sender and returns.
    /// </summary>
    /// <remarks>
    /// Synchronous by nature: a channel write. Whatever the backplane is doing, the caller - which
    /// has already saved the thing being announced - pays nothing for it.
    /// </remarks>
    private Task SendAsync<TPayload>(
        IReadOnlyList<string> groups,
        string method,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || payload is null)
        {
            return Task.CompletedTask;
        }

        if (!_queue.TryEnqueue(new RealtimePush(groups, method, payload)))
        {
            // The queue drops the oldest when it is full, so this is close to unreachable; it
            // matters only as the signal that the sender has stopped keeping up.
            LogPushDropped(method, groups.Count);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Warning,
        Message = "Realtime push of {Method} to {GroupCount} group(s) was dropped: the sender is behind.")]
    private partial void LogPushDropped(string method, int groupCount);
}
