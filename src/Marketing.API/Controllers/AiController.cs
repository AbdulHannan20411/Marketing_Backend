using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Ai;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>The AI marketing assistant.</summary>
/// <remarks>
/// The browser sends a prompt here and receives text. It never learns which provider answered, and
/// the provider key never leaves the server.
/// <para>
/// Three gates, all enforced here rather than by the client hiding a tab: the caller holds
/// <see cref="Permissions.Ai.AssistantUse"/>, the workspace plan includes the AI module, and the
/// per-user rate limit has room. The prompt itself is validated by <c>AiGenerateRequestValidator</c>
/// before the action runs, so a rejected prompt spends no provider quota.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/ai")]
[Authorize]
[RequireModule(PlanModules.Ai)]
public sealed class AiController : ApiControllerBase
{
    private readonly IAiService _ai;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public AiController(IAiService ai, ITenantScopeResolver scope)
    {
        _ai = ai;
        _scope = scope;
    }

    /// <summary>Generates text for a prompt.</summary>
    /// <param name="request">The prompt.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The generated answer.</response>
    /// <response code="409">The assistant is not configured, or the provider withheld an answer.</response>
    /// <response code="422">The prompt is empty or too long.</response>
    /// <response code="429">The caller's, or the platform's, AI allowance is exhausted for now.</response>
    /// <response code="502">The provider rejected the request.</response>
    /// <response code="503">The provider is unavailable or timed out.</response>
    [HttpPost("generate")]
    [RequirePermission(Permissions.Ai.AssistantUse)]
    [EnableRateLimiting(AppConstants.RateLimits.AiGenerate)]
    [ProducesResponseType(typeof(ApiResponse<AiGenerateResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GenerateAsync(
        [FromBody] AiGenerateRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var answer = await _ai.GenerateAsync(request.Prompt.Trim(), cancellationToken);

        return Success(new AiGenerateResponse(answer));
    }
}
