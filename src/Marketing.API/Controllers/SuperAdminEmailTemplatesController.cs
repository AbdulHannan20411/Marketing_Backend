using Asp.Versioning;
using Marketing.Application.DTOs.Email;
using Marketing.Application.Services.Email;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>
/// The platform's transactional email templates.
/// </summary>
/// <remarks>
/// Super Admin only, and platform data rather than tenant data: every workspace's users receive the
/// same wording, so no route takes an <c>adminId</c> and no tenant is involved.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/superadmin/email-templates")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class SuperAdminEmailTemplatesController : ApiControllerBase
{
    private readonly IEmailTemplateAdminService _templates;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="templates">Template administration.</param>
    public SuperAdminEmailTemplatesController(IEmailTemplateAdminService templates)
    {
        _templates = templates;
    }

    /// <summary>Returns every template without its bodies, in the shipped order.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The templates.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmailTemplateSummaryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken) =>
        Success(await _templates.ListAsync(cancellationToken));

    /// <summary>Returns one template in full.</summary>
    /// <param name="key">Template key, for example <c>auth.invitation</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The template.</response>
    /// <response code="404">No such template.</response>
    [HttpGet("{key}")]
    [ProducesResponseType(typeof(ApiResponse<EmailTemplateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(string key, CancellationToken cancellationToken) =>
        Success(await _templates.GetAsync(key, cancellationToken));

    /// <summary>Saves new wording for a template.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="request">The subject and both bodies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The saved template.</response>
    /// <response code="404">No such template.</response>
    /// <response code="422">The draft would not render; every problem is listed under <c>Template</c>.</response>
    [HttpPut("{key}")]
    [ProducesResponseType(typeof(ApiResponse<EmailTemplateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateAsync(
        string key,
        [FromBody] EmailTemplateDraftRequest request,
        CancellationToken cancellationToken) =>
        Success(await _templates.UpdateAsync(key, request, cancellationToken), "Template saved.");

    /// <summary>Sends an unsaved draft to the signed-in staff member.</summary>
    /// <remarks>
    /// Rendered with each variable's sample and the real system values. There is no recipient field on
    /// purpose. Limited separately from the rest of this controller, because every call puts a real
    /// message through the outbox.
    /// </remarks>
    /// <param name="key">Template key.</param>
    /// <param name="request">The draft to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Where the test went.</response>
    /// <response code="404">No such template.</response>
    /// <response code="422">The draft would not render.</response>
    [HttpPost("{key}/test")]
    [EnableRateLimiting(AppConstants.RateLimits.EmailTests)]
    [ProducesResponseType(typeof(ApiResponse<TestEmailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SendTestAsync(
        string key,
        [FromBody] EmailTemplateDraftRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _templates.SendTestAsync(key, request, cancellationToken);

        return Success(result, $"Test email sent to {result.SentTo}.");
    }

    /// <summary>Returns a template to the wording it shipped with.</summary>
    /// <param name="key">Template key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The template, back to its shipped wording.</response>
    /// <response code="404">No such template.</response>
    /// <response code="409">The template has no shipped default.</response>
    [HttpPost("{key}/reset")]
    [ProducesResponseType(typeof(ApiResponse<EmailTemplateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetAsync(string key, CancellationToken cancellationToken) =>
        Success(await _templates.ResetAsync(key, cancellationToken), "Template reset to the shipped version.");
}
