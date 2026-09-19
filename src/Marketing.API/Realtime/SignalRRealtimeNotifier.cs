using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Realtime;

/// <summary>SignalR implementation of <see cref="IRealtimeNotifier"/>.</summary>
public sealed partial class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly ILogger<SignalRRealtimeNotifier> _logger;

    /// <summary>Initialises a new instance.</summary>
    public SignalRRealtimeNotifier(IHubContext<RealtimeHub> hub, ILogger<SignalRRealtimeNotifier> logger)
    {
        _hub = hub;
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
    /// <summary>Pushes to each person's own group, and to nobody else.</summary>
    private async Task SendToUsersAsync<TPayload>(
        IReadOnlyCollection<long> userIds,
        string method,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        var groups = userIds.Select(RealtimeHub.UserGroup).ToList();

        try
        {
            await _hub.Clients.Groups(groups).SendAsync(method, payload, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPushFailed(exception, method, $"{groups.Count} users");
        }
    }

    private async Task SendAsync<TPayload>(
        string group,
        string method,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.Group(group).SendAsync(method, payload, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPushFailed(exception, method, group);
        }
    }

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Realtime push of {Method} to {Group} failed; clients will see it on their next fetch.")]
    private partial void LogPushFailed(Exception exception, string method, string group);
}
