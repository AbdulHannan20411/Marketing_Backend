using Asp.Versioning;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.Services.Payments;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>
/// Reviewing manual payments, from the platform's side.
/// </summary>
/// <remarks>
/// Gated on the Super Admin policy rather than on a permission, and that distinction is the
/// security boundary of the whole feature: a permission is something a tenant administrator could
/// be granted, and approval is the one action in the platform that turns a customer's uploaded
/// image into a paid entitlement.
/// <para>
/// <c>adminId</c> is meaningless here and is deliberately not accepted. These endpoints already
/// read across every tenant.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/superadmin/payment-requests")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class SuperAdminPaymentRequestsController : ApiControllerBase
{
    private readonly IPaymentReviewService _review;

    /// <summary>Initialises a new instance.</summary>
    public SuperAdminPaymentRequestsController(IPaymentReviewService review)
    {
        _review = review;
    }

    /// <summary>Returns the review queue, newest first.</summary>
    /// <remarks>
    /// <c>status</c> accepts any payment status or <c>all</c>; <c>search</c> matches the
    /// organisation or the submitter's email, case-insensitively.
    /// </remarks>
    /// <param name="query">Paging, search and the status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of submitted payments.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<PaymentRequestResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] PaymentRequestQuery query,
        CancellationToken cancellationToken) =>
        SuccessPage(await _review.GetQueueAsync(query, cancellationToken));

    /// <summary>Returns one submitted payment.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The request.</response>
    /// <response code="404">No such request.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<PaymentRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(string id, CancellationToken cancellationToken) =>
        Success(await _review.GetAsync(id, cancellationToken));

    /// <summary>Returns a submitted payment's proof.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">No such request.</response>
    [HttpGet("{id}/proof")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProofAsync(string id, CancellationToken cancellationToken)
    {
        var file = await _review.OpenProofAsync(id, cancellationToken);

        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Accepts a payment and grants the plan.</summary>
    /// <remarks>
    /// The only endpoint in the platform that grants a plan from a customer-initiated action. The
    /// grant, the invoice, the payment record and the status stamp commit in one transaction, and
    /// the pending check happens inside it — so a double-click, a retry, or two reviewers working
    /// the same queue grant the plan exactly once.
    /// </remarks>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The approved request.</response>
    /// <response code="409">Already decided, or the plan is no longer available.</response>
    [HttpPost("{id}/approve")]
    [ProducesResponseType(typeof(ApiResponse<PaymentRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveAsync(string id, CancellationToken cancellationToken) =>
        Success(await _review.ApproveAsync(id, cancellationToken), "Payment approved and plan granted.");

    /// <summary>Refuses a payment, with a reason the customer is told.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="request">The reason. Required, at least ten characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The rejected request.</response>
    /// <response code="409">It has already been decided.</response>
    /// <response code="422">The reason is missing or too short.</response>
    [HttpPost("{id}/reject")]
    [ProducesResponseType(typeof(ApiResponse<PaymentRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RejectAsync(
        string id,
        [FromBody] RejectPaymentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Success(await _review.RejectAsync(id, request.Reason, cancellationToken), "Payment rejected.");
    }
}
