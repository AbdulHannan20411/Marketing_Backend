using Marketing.Application.DTOs.Billing;

namespace Marketing.Application.Interfaces;

/// <summary>Subscription and billing reads and lifecycle actions for the resolved tenant.</summary>
public interface IBillingService
{
    /// <summary>
    /// Returns the caller's subscription, its plan, and live usage counts.
    /// <para>
    /// Usage is computed on the request, not read from a rollup: the client renders every gauge
    /// and upgrade prompt from it, and a stale figure either blocks a customer who has room or
    /// lets one sail past a limit.
    /// </para>
    /// </summary>
    public Task<SubscriptionSnapshot> GetSubscriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns what the workspace is allowed to do, without anything about what it pays.
    /// </summary>
    /// <remarks>
    /// Readable by any member. The shell fetches it on every page load to decide which navigation
    /// and features exist, so gating it on a billing permission left every employee running with
    /// entitlements permanently unknown and a 403 on each load.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<EntitlementsSnapshot> GetEntitlementsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the plans a customer may buy. Excludes inactive and archived.</summary>
    public Task<IReadOnlyList<SubscriptionPlanResponse>> GetPurchasablePlansAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Returns invoices, payments and renewals for the caller's tenant.</summary>
    public Task<BillingHistory> GetBillingHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves the subscription to another plan.</summary>
    /// <remarks>
    /// Upgrades apply immediately; downgrades take effect at period end. A downgrade whose limits
    /// sit below current usage is refused with a 409 naming the offending metric.
    /// </remarks>
    public Task<SubscriptionSnapshot> ChangePlanAsync(
        ChangePlanRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the subscription at the end of the current period.</summary>
    public Task<SubscriptionSnapshot> CancelAsync(
        CancelSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reverses a pending cancellation before the period ends.</summary>
    public Task<SubscriptionSnapshot> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Switches automatic renewal on or off.</summary>
    public Task<SubscriptionSnapshot> SetAutoRenewAsync(
        AutoRenewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders one invoice as a downloadable document.
    /// </summary>
    /// <remarks>
    /// Loaded through the tenant-filtered repository, so another workspace's identifier is a
    /// <see cref="Common.Exceptions.NotFoundException"/> rather than a refusal — the two are
    /// indistinguishable to a caller, which is what stops identifiers being probed for existence.
    /// </remarks>
    /// <param name="invoiceId">Prefixed invoice identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered bytes, the file name and its media type.</returns>
    public Task<RenderedInvoice> RenderInvoiceAsync(
        string invoiceId,
        CancellationToken cancellationToken = default);

    /// <summary>Retries payment for an unsettled invoice.</summary>
    /// <param name="invoiceId">Invoice identifier.</param>
    /// <param name="idempotencyKey">Caller-supplied key that makes a retry safe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<InvoiceResponse> PayInvoiceAsync(
        string invoiceId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Plan administration. Platform staff only.</summary>
public interface IPlanManagementService
{
    /// <summary>Returns every plan, including inactive and archived ones.</summary>
    public Task<IReadOnlyList<SubscriptionPlanResponse>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a plan.</summary>
    public Task<SubscriptionPlanResponse> CreateAsync(PlanDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Applies a partial update, leaving omitted fields untouched.</summary>
    public Task<SubscriptionPlanResponse> UpdateAsync(
        string planId,
        PlanPatch patch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a plan with a new identifier, a " (copy)" suffix, inactive status and both badge
    /// flags cleared.
    /// </summary>
    public Task<SubscriptionPlanResponse> DuplicateAsync(string planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a plan.
    /// <para>
    /// Never orphans a subscriber: a plan with live subscriptions is archived so it stops being
    /// offered while existing terms are honoured, rather than deleted.
    /// </para>
    /// </summary>
    public Task DeleteAsync(string planId, CancellationToken cancellationToken = default);
}

/// <summary>A rendered invoice, ready to be written to the response.</summary>
/// <param name="Content">The document bytes.</param>
/// <param name="FileName">Name the browser saves it under — the invoice number.</param>
/// <param name="ContentType">Media type.</param>
public sealed record RenderedInvoice(byte[] Content, string FileName, string ContentType);
