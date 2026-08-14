using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Payments;
using Marketing.Common.Constants;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.API.Controllers;

/// <summary>
/// Paying by bank transfer or mobile wallet, from the customer's side.
/// </summary>
/// <remarks>
/// <b>Nothing on this controller grants a plan.</b> Submitting records an intent and a file; the
/// entitlement moves only when a platform administrator approves the request, which lives on a
/// Super Admin-only controller. A customer who can upload an image must not be able to upgrade
/// themselves, and that is enforced by the route split rather than by a permission a tenant could
/// be granted.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/billing")]
[Authorize]
public sealed class BillingPaymentRequestsController : ApiControllerBase
{
    private readonly IPaymentRequestService _payments;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public BillingPaymentRequestsController(IPaymentRequestService payments, ITenantScopeResolver scope)
    {
        _payments = payments;
        _scope = scope;
    }

    /// <summary>Returns the channels a customer can pay through.</summary>
    /// <remarks>
    /// Served by the API rather than compiled into the client, because account numbers change and a
    /// stale one in a shipped bundle means money going nowhere, with a receipt to prove it was sent.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The active channels.</response>
    [HttpGet("payment-channels")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PaymentChannelDetails>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetChannelsAsync(CancellationToken cancellationToken) =>
        Success(await _payments.GetChannelsAsync(cancellationToken));

    /// <summary>Returns a channel's QR code.</summary>
    /// <param name="channel">Channel whose code is wanted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The image.</response>
    /// <response code="404">No such channel, or it has no code uploaded.</response>
    [HttpGet("payment-channels/{channel}/qr")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChannelQrAsync(
        PaymentChannel channel,
        CancellationToken cancellationToken)
    {
        var file = await _payments.OpenChannelQrAsync(channel, cancellationToken);

        return File(file.Content, file.ContentType);
    }

    /// <summary>Records a claim of payment and its proof.</summary>
    /// <remarks>
    /// The amount is derived on the server from the plan and cycle and is never read from the
    /// request, so a customer cannot submit a token sum and hope a reviewer skims past it.
    /// </remarks>
    /// <param name="request">The submission.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="201">The recorded request, awaiting review.</response>
    /// <response code="409">This workspace already has a payment awaiting review.</response>
    /// <response code="422">No file, wrong type, over the size limit, or an unknown plan.</response>
    [HttpPost("payment-requests")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [EnableRateLimiting(AppConstants.RateLimits.Uploads)]
    [ProducesResponseType(typeof(ApiResponse<PaymentRequestResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SubmitAsync(
        [FromForm] SubmitPaymentForm request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        if (request.Proof is not { Length: > 0 })
        {
            throw new Common.Exceptions.ValidationException(
                "proof", "Attach a screenshot or PDF of your payment receipt.");
        }

        await using var content = request.Proof.OpenReadStream();

        var created = await _payments.SubmitAsync(
            new SubmitPaymentCommand(
                request.PlanId ?? string.Empty,
                request.BillingCycle,
                request.Channel,
                request.Reference,
                request.Note,
                request.Proof.FileName,
                request.Proof.ContentType,
                content,
                request.Proof.Length),
            cancellationToken);

        return SuccessCreated(nameof(GetAsync), new { id = created.Id }, created);
    }

    /// <summary>Returns this workspace's submissions, newest first.</summary>
    /// <param name="request">Paging.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of submissions.</response>
    [HttpGet("payment-requests")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<PaymentRequestResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMineAsync(
        [FromQuery] PageRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _payments.GetMineAsync(request, cancellationToken));
    }

    /// <summary>Returns one of this workspace's submissions.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The submission.</response>
    /// <response code="404">No such request, or it belongs to another workspace.</response>
    [HttpGet("payment-requests/{id}")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<PaymentRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _payments.GetMineAsync(id, cancellationToken));
    }

    /// <summary>Withdraws a submission that has not been reviewed.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The withdrawn request.</response>
    /// <response code="409">It has already been approved or rejected.</response>
    [HttpPost("payment-requests/{id}/cancel")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(typeof(ApiResponse<PaymentRequestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _payments.CancelAsync(id, cancellationToken), "Payment withdrawn.");
    }

    /// <summary>Returns the uploaded proof.</summary>
    /// <remarks>
    /// Readable by the owning workspace only. Another tenant's identifier returns 404 rather than
    /// 403, so identifiers cannot be probed for existence.
    /// </remarks>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">No such request, or it belongs to another workspace.</response>
    [HttpGet("payment-requests/{id}/proof")]
    [RequirePermission(Permissions.Settings.Subscription)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProofAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var file = await _payments.OpenProofAsync(id, cancellationToken);

        return File(file.Content, file.ContentType, file.FileName);
    }
}

/// <summary>The multipart body of a payment submission.</summary>
/// <remarks>
/// There is deliberately no amount field. It is derived from the plan and cycle on the server; a
/// client-supplied amount is a client-supplied invoice.
/// </remarks>
public sealed class SubmitPaymentForm
{
    /// <summary>Prefixed identifier of the plan being bought.</summary>
    public string? PlanId { get; init; }

    /// <summary><c>Monthly</c> or <c>Yearly</c>.</summary>
    public string? BillingCycle { get; init; }

    /// <summary><c>JazzCash</c>, <c>EasyPaisa</c> or <c>BankTransfer</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>Transaction reference from the customer's receipt.</summary>
    public string? Reference { get; init; }

    /// <summary>Free text from the customer.</summary>
    public string? Note { get; init; }

    /// <summary>The receipt. PNG, JPG, WEBP or PDF.</summary>
    public IFormFile? Proof { get; init; }
}
