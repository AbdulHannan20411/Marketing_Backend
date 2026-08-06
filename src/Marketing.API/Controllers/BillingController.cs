using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Billing;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>The caller's subscription.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/subscription")]
[Authorize]
public sealed class SubscriptionController : ApiControllerBase
{
    private readonly IBillingService _billing;

    /// <summary>Initialises a new instance.</summary>
    public SubscriptionController(IBillingService billing) => _billing = billing;

    /// <summary>Returns the subscription, its plan and live usage counts.</summary>
    /// <response code="200">The subscription snapshot.</response>
    [HttpGet]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionSnapshot>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken) =>
        Success(await _billing.GetSubscriptionAsync(cancellationToken));

    /// <summary>Moves to another plan.</summary>
    /// <remarks>
    /// Upgrades apply immediately, downgrades at period end. A downgrade below current usage is
    /// refused with a 409 naming the offending metric.
    /// </remarks>
    /// <response code="200">The updated subscription.</response>
    /// <response code="409">The target plan's limits are below current usage.</response>
    [HttpPost("change-plan")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangePlanAsync(
        [FromBody] ChangePlanRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _billing.ChangePlanAsync(request, cancellationToken);

        return Success(snapshot, $"Moved to the {snapshot.Plan.Name} plan.");
    }

    /// <summary>Cancels the subscription at the end of the current period.</summary>
    /// <response code="200">The updated subscription.</response>
    [HttpPost("cancel")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionSnapshot>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelAsync(
        [FromBody] CancelSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _billing.CancelAsync(request, cancellationToken);

        return Success(snapshot, "Subscription cancelled. Access continues until the period ends.");
    }

    /// <summary>Switches automatic renewal on or off.</summary>
    /// <response code="200">The updated subscription.</response>
    [HttpPost("auto-renew")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionSnapshot>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetAutoRenewAsync(
        [FromBody] AutoRenewRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await _billing.SetAutoRenewAsync(request, cancellationToken);

        return Success(snapshot, request.Enabled ? "Automatic renewal is on." : "Automatic renewal is off.");
    }
}

/// <summary>Plans a customer may buy. Drives the pricing page.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/plans")]
[Authorize]
public sealed class PlansController : ApiControllerBase
{
    private readonly IBillingService _billing;

    /// <summary>Initialises a new instance.</summary>
    public PlansController(IBillingService billing) => _billing = billing;

    /// <summary>Returns purchasable plans, excluding inactive and archived ones.</summary>
    /// <remarks>Available to any authenticated user - it drives the pricing page.</remarks>
    /// <response code="200">The plans.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SubscriptionPlanResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken) =>
        Success(await _billing.GetPurchasablePlansAsync(cancellationToken));
}

/// <summary>Invoices, payments and renewals.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/billing")]
[Authorize]
public sealed class BillingController : ApiControllerBase
{
    private readonly IBillingService _billing;

    /// <summary>Initialises a new instance.</summary>
    public BillingController(IBillingService billing) => _billing = billing;

    /// <summary>Returns the billing history.</summary>
    /// <response code="200">Invoices, payments and renewals.</response>
    [HttpGet("history")]
    [RequirePermission(Permissions.Settings.Billing)]
    [ProducesResponseType(typeof(ApiResponse<BillingHistory>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistoryAsync(CancellationToken cancellationToken) =>
        Success(await _billing.GetBillingHistoryAsync(cancellationToken));

    /// <summary>Retries payment for an unsettled invoice.</summary>
    /// <remarks>
    /// Send an <c>Idempotency-Key</c> header. Without one, a retried request after a timeout can
    /// charge the customer twice.
    /// </remarks>
    /// <param name="id">Invoice identifier.</param>
    /// <param name="idempotencyKey">Key that makes a retry safe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The settled invoice.</response>
    /// <response code="409">The payment was declined.</response>
    [HttpPost("invoices/{id}/pay")]
    [RequirePermission(Permissions.Settings.Billing)]
    [ProducesResponseType(typeof(ApiResponse<InvoiceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PayInvoiceAsync(
        string id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var invoice = await _billing.PayInvoiceAsync(id, idempotencyKey, cancellationToken);

        return Success(invoice, $"Invoice {invoice.Number} settled.");
    }

    /// <summary>Downloads an invoice as a PDF.</summary>
    /// <remarks>
    /// Not implemented. The download URL and its short-lived signed-token flow are in place, but
    /// the renderer is deliberately deferred - it needs an invoice layout and a licence decision
    /// on the PDF library. The client integration will not change when it lands.
    /// </remarks>
    /// <param name="id">Invoice identifier.</param>
    /// <response code="501">The renderer is not built yet.</response>
    [HttpGet("invoices/{id}/pdf")]
    [RequirePermission(Permissions.Settings.Billing)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public IActionResult GetInvoicePdf(string id) =>
        Problem(
            title: "Invoice rendering is not available yet",
            detail: "Invoice PDF generation has not been built. The invoice data is available from /billing/history.",
            statusCode: StatusCodes.Status501NotImplemented);
}

/// <summary>Plan administration. Platform staff only.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/plans")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
public sealed class PlanAdministrationController : ApiControllerBase
{
    private readonly IPlanManagementService _plans;

    /// <summary>Initialises a new instance.</summary>
    public PlanAdministrationController(IPlanManagementService plans) => _plans = plans;

    /// <summary>Returns every plan, including inactive and archived ones.</summary>
    /// <response code="200">The plans.</response>
    [HttpGet]
    [RequirePermission(Permissions.Platform.Plans)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SubscriptionPlanResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken) =>
        Success(await _plans.GetAllAsync(cancellationToken));

    /// <summary>Creates a plan.</summary>
    /// <response code="200">The created plan.</response>
    [HttpPost]
    [RequirePermission(Permissions.Platform.Plans)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionPlanResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] PlanDraft draft,
        CancellationToken cancellationToken)
    {
        var plan = await _plans.CreateAsync(draft, cancellationToken);

        return Success(plan, $"Plan \"{plan.Name}\" created.");
    }

    /// <summary>Applies a partial update.</summary>
    /// <remarks>Only supplied fields are applied; omitted fields are left untouched.</remarks>
    /// <param name="id">Plan identifier.</param>
    /// <param name="patch">Fields to change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated plan.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Platform.Plans)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionPlanResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] PlanPatch patch,
        CancellationToken cancellationToken)
    {
        var plan = await _plans.UpdateAsync(id, patch, cancellationToken);

        return Success(plan, $"Plan \"{plan.Name}\" saved.");
    }

    /// <summary>Duplicates a plan as an inactive copy.</summary>
    /// <param name="id">Plan to copy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The copy.</response>
    [HttpPost("{id}/duplicate")]
    [RequirePermission(Permissions.Platform.Plans)]
    [ProducesResponseType(typeof(ApiResponse<SubscriptionPlanResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DuplicateAsync(string id, CancellationToken cancellationToken)
    {
        var plan = await _plans.DuplicateAsync(id, cancellationToken);

        return Success(plan, $"Plan duplicated as \"{plan.Name}\".");
    }

    /// <summary>Retires a plan.</summary>
    /// <remarks>
    /// A plan with live subscribers is archived rather than deleted, so their terms are honoured
    /// while the plan stops being offered.
    /// </remarks>
    /// <param name="id">Plan identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The plan was retired.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Platform.Plans)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _plans.DeleteAsync(id, cancellationToken);

        return SuccessEmpty("Plan retired.");
    }
}
