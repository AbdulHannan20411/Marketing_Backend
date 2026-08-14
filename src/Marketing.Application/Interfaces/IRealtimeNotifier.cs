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

    /// <summary>
    /// Pushes import progress to a tenant.
    /// <para>
    /// Sent as the worker advances. Without it the wizard has to poll the detail endpoint every
    /// second or two for the whole of a multi-minute run, which is a request per second per
    /// watching operator for a number that is already known here.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant that owns the import.</param>
    /// <param name="progress">Current state of the import.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PublishImportProgressAsync(
        long tenantId,
        ImportProgress progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes a manual payment's state to the submitting workspace and to platform reviewers.
    /// </summary>
    /// <remarks>
    /// Two audiences, one call, because both need the same event: the review queue refreshes and
    /// the customer's subscription page re-reads its entitlements without a reload. The tenant is
    /// passed explicitly rather than read from ambient context, because a reviewer's decision runs
    /// under no tenant of its own.
    /// </remarks>
    /// <param name="tenantId">Workspace that submitted it, or null if it has none.</param>
    /// <param name="payment">Current state of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PublishPaymentRequestAsync(
        long? tenantId,
        DTOs.Payments.PaymentRequestEvent payment,
        CancellationToken cancellationToken = default);
}

/// <summary>An import's live state, as pushed to the wizard.</summary>
/// <param name="BatchId">Opaque import identifier.</param>
/// <param name="Status">Where the import has got to.</param>
/// <param name="ProgressPercent">Real progress through the current stage.</param>
/// <param name="Statistics">Row counters as they currently stand.</param>
/// <param name="FailureReason">Why the import failed, when it did.</param>
public sealed record ImportProgress(
    string BatchId,
    DTOs.Imports.BatchStatus Status,
    int ProgressPercent,
    DTOs.Imports.ImportStatistics Statistics,
    string? FailureReason);

/// <summary>Names of the methods the client subscribes to. Shared with the Angular client.</summary>
public static class RealtimeEvents
{
    /// <summary>A new notification arrived.</summary>
    public const string NotificationReceived = "notificationReceived";

    /// <summary>A campaign's counters changed.</summary>
    public const string CampaignProgress = "campaignProgress";

    /// <summary>An import's status or counters changed.</summary>
    public const string ImportProgress = "importProgress";

    /// <summary>A manual payment was submitted or decided.</summary>
    public const string PaymentRequestUpdated = "paymentRequestUpdated";
}
