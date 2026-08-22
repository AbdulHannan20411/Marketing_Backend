using Asp.Versioning;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Services.Workspace;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>Workspace-level actions its owner can take.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/workspace")]
[Authorize]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class WorkspaceController : ApiControllerBase
{
    private readonly IWorkspaceDeactivationService _deactivation;

    /// <summary>Initialises a new instance.</summary>
    public WorkspaceController(IWorkspaceDeactivationService deactivation) => _deactivation = deactivation;

    /// <summary>Switches the caller's whole workspace off.</summary>
    /// <remarks>
    /// Deactivation, not deletion: access ends for everyone in the workspace and the data is kept.
    /// <para>
    /// Every session for every member is revoked, scheduled campaigns stop firing, and the
    /// subscription stops renewing - all in one transaction, because a workspace marked off whose
    /// campaigns kept sending is the worst outcome this endpoint has.
    /// </para>
    /// <para>
    /// Owner only, enforced here rather than by the client hiding a card, and the password is
    /// re-verified: it is the only thing between a session left open on a shared laptop and a
    /// switched-off company. No workspace identifier is accepted, ever.
    /// </para>
    /// </remarks>
    /// <param name="request">Reason, details and the caller's password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">When access ended, and how long the data is kept.</response>
    /// <response code="403">The caller does not own the workspace.</response>
    /// <response code="409">The workspace is already deactivated.</response>
    /// <response code="422">The password, reason or details were not acceptable.</response>
    [HttpPost("deactivate")]
    [ProducesResponseType(typeof(ApiResponse<WorkspaceDeactivationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> DeactivateAsync(
        [FromBody] WorkspaceDeactivationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _deactivation.DeactivateAsync(request, cancellationToken);

        return Success(result, "Your workspace has been deactivated.");
    }
}
