using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>Automatic replies for the resolved tenant.</summary>
/// <remarks>
/// Gated on the AI module as well as on permissions. A workspace whose plan does not include the
/// assistant should be told that plainly on the settings screen rather than allowed to configure
/// something that will never run.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/whatsapp/auto-reply")]
[Authorize]
[RequireModule(PlanModules.Ai)]
public sealed class AutoReplyController : ApiControllerBase
{
    private readonly IAutoReplyService _autoReplies;
    private readonly IAutoReplyKnowledgeService _knowledge;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public AutoReplyController(
        IAutoReplyService autoReplies,
        IAutoReplyKnowledgeService knowledge,
        ITenantScopeResolver scope)
    {
        _autoReplies = autoReplies;
        _knowledge = knowledge;
        _scope = scope;
    }

    /// <summary>Returns the rules, what the plan allows, and what is left of the allowance.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The current settings.</response>
    [HttpGet]
    [RequirePermission(Permissions.Ai.AutoReplyManage)]
    [ProducesResponseType(typeof(ApiResponse<AutoReplySettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _autoReplies.GetAsync(cancellationToken));
    }

    /// <summary>Saves the rules.</summary>
    /// <param name="request">The new rules.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The saved settings.</response>
    /// <response code="409">
    /// A trigger the plan does not sell (<c>auto_reply_trigger_not_in_plan</c>).
    /// </response>
    /// <response code="422">A delay, wait or ceiling outside the allowed range.</response>
    [HttpPut]
    [RequirePermission(Permissions.Ai.AutoReplyManage)]
    [ProducesResponseType(typeof(ApiResponse<AutoReplySettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] AutoReplySettingsRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _autoReplies.UpdateAsync(request, cancellationToken),
            "Automatic replies updated.");
    }

    /// <summary>Returns the knowledge file's entries and the fallback.</summary>
    /// <remarks>
    /// Never a 404: a workspace that has never uploaded gets no entries and the default fallback, since
    /// the client reads a 404 as "this API has no knowledge endpoint".
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The knowledge.</response>
    [HttpGet("knowledge")]
    [RequirePermission(Permissions.Ai.AutoReplyManage)]
    [ProducesResponseType(typeof(ApiResponse<AutoReplyKnowledgeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetKnowledgeAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _knowledge.GetAsync(cancellationToken));
    }

    /// <summary>Replaces every knowledge entry, and the fallback, in one transaction.</summary>
    /// <param name="request">The rows read from the spreadsheet, and the fallback.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The stored knowledge.</response>
    /// <response code="422">A row or the fallback breaks a rule; each problem is keyed by field.</response>
    [HttpPut("knowledge")]
    [RequirePermission(Permissions.Ai.AutoReplyManage)]
    [RequestSizeLimit(2_000_000)]
    [ProducesResponseType(typeof(ApiResponse<AutoReplyKnowledgeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceKnowledgeAsync(
        [FromBody] AutoReplyKnowledgeRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _knowledge.ReplaceAsync(request, cancellationToken), "Knowledge saved.");
    }

    /// <summary>Removes every knowledge entry, keeping the fallback.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">Removed.</response>
    [HttpDelete("knowledge")]
    [RequirePermission(Permissions.Ai.AutoReplyManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearKnowledgeAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _knowledge.ClearAsync(cancellationToken);

        return NoContent();
    }
}
