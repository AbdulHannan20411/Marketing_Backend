using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Workspace;

namespace Marketing.Application.Interfaces;

/// <summary>
/// Pushes live updates to connected clients.
/// <para>
/// An abstraction rather than a direct SignalR dependency, so services stay testable and the
/// Application layer does not take a dependency on the transport. If the transport ever changes -
/// SignalR to raw WebSockets, or an outbox-backed publisher - only the implementation moves.
/// </para>
/// <para>
/// Every method is best-effort. A push that fails must never fail the request that triggered it:
/// the data is already committed, and the client will pick it up on its next fetch.
/// </para>
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>Pushes a new notification to one user.</summary>
    /// <param name="userId">Recipient.</param>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task NotifyUserAsync(
        long userId,
        AppNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>Pushes a notification to every connected member of a tenant.</summary>
    /// <param name="tenantId">Tenant.</param>
    /// <param name="notification">The notification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task NotifyTenantAsync(
        long tenantId,
        AppNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes campaign progress to a tenant.
    /// <para>
    /// Sent as the dispatcher advances, which is what lets the campaign screen show a live counter
    /// instead of polling for a number that changes every few hundred milliseconds.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant that owns the campaign.</param>
    /// <param name="campaign">Current campaign state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PublishCampaignProgressAsync(
        long tenantId,
        CampaignResponse campaign,
        CancellationToken cancellationToken = default);
}

/// <summary>Names of the methods the client subscribes to. Shared with the Angular client.</summary>
public static class RealtimeEvents
{
    /// <summary>A new notification arrived.</summary>
    public const string NotificationReceived = "notificationReceived";

    /// <summary>A campaign's counters changed.</summary>
    public const string CampaignProgress = "campaignProgress";
}
