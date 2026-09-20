using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Realtime;

/// <summary>SignalR implementation of <see cref="IRealtimeNotifier"/>.</summary>
public sealed partial class SignalRRealtimeNotifier : IRealtimeNotifier
{
    /// <summary>
    /// How long a push may take before the caller stops waiting for it.
    /// </summary>
    /// <remarks>
    /// A push is a courtesy: the data is already saved, and every screen fetches it anyway. Waiting
    /// on it puts the backplane's health inside the user's save button, which is how a campaign save
    /// once took forty seconds against an unreachable Redis.
    /// </remarks>
    private static readonly TimeSpan PushBudget = TimeSpan.FromMilliseconds(750);

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
        var description = $"{groups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} users";

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        budget.CancelAfter(PushBudget);

        try
        {
            await _hub.Clients.Groups(groups).SendAsync(method, payload, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogPushTimedOut(method, description);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPushFailed(exception, method, description);
        }
    }

    private async Task SendAsync<TPayload>(
        string group,
        string method,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        budget.CancelAfter(PushBudget);

        try
        {
            await _hub.Clients.Group(group).SendAsync(method, payload, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own work is done and saved. Clients see this on their next fetch.
            LogPushTimedOut(method, group);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPushFailed(exception, method, group);
        }
    }

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Realtime push of {Method} to {Group} took too long and was abandoned; clients will see it on their next fetch.")]
    private partial void LogPushTimedOut(string method, string group);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Realtime push of {Method} to {Group} failed; clients will see it on their next fetch.")]
    private partial void LogPushFailed(Exception exception, string method, string group);
}
