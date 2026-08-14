using Asp.Versioning;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.Services.Payments;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.API.Controllers;

/// <summary>
/// Configuring where customers send money.
/// </summary>
/// <remarks>
/// Super Admin only. These fields decide where a customer's money goes, which makes changing an
/// account number as consequential as approving a payment — neither is something a tenant
/// administrator may do.
/// <para>
/// The three channels are seeded inactive with placeholder details, so the checkout screen renders
/// but offers nothing until someone sets real account details here. That is deliberate: a
/// plausible-looking wrong account number is worse than an absent one.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/superadmin/payment-channels")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class SuperAdminPaymentChannelsController : ApiControllerBase
{
    private readonly IPaymentChannelAdminService _channels;

    /// <summary>Initialises a new instance.</summary>
    public SuperAdminPaymentChannelsController(IPaymentChannelAdminService channels)
    {
        _channels = channels;
    }

    /// <summary>Returns every channel, including the ones not currently offered.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The channels.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PaymentChannelDetails>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken) =>
        Success(await _channels.GetAllAsync(cancellationToken));

    /// <summary>Updates a channel's account details and wording.</summary>
    /// <param name="channel">Channel to update.</param>
    /// <param name="draft">New values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated channel.</response>
    /// <response code="404">No such channel.</response>
    /// <response code="422">The account details are incomplete.</response>
    [HttpPut("{channel}")]
    [ProducesResponseType(typeof(ApiResponse<PaymentChannelDetails>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateAsync(
        PaymentChannel channel,
        [FromBody] PaymentChannelDraft draft,
        CancellationToken cancellationToken) =>
        Success(await _channels.UpdateAsync(channel, draft, cancellationToken), "Payment channel updated.");

    /// <summary>Replaces a channel's QR image.</summary>
    /// <param name="channel">Channel to update.</param>
    /// <param name="qr">The image. PNG, JPG or WEBP, up to 2 MB.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated channel.</response>
    /// <response code="422">The file is missing, too large, or of a type we do not accept.</response>
    [HttpPost("{channel}/qr")]
    [EnableRateLimiting(AppConstants.RateLimits.Uploads)]
    [ProducesResponseType(typeof(ApiResponse<PaymentChannelDetails>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadQrAsync(
        PaymentChannel channel,
        IFormFile? qr,
        CancellationToken cancellationToken)
    {
        if (qr is not { Length: > 0 })
        {
            throw new Common.Exceptions.ValidationException("qr", "Choose a QR image to upload.");
        }

        await using var content = qr.OpenReadStream();

        var updated = await _channels.UploadQrAsync(
            channel, qr.FileName, content, qr.Length, cancellationToken);

        return Success(updated, "QR code updated.");
    }
}
