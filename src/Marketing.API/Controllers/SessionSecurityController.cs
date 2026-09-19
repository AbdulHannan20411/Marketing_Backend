using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Security;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Security;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>The signed-in person's own sessions: the heartbeat, their devices, and ending one.</summary>
/// <remarks>
/// Separate from the sign-in endpoints so it does not share their rate limit, which is tuned against
/// password guessing and would throttle a heartbeat sent every minute.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[Authorize]
public sealed class AccountSessionsController : ApiControllerBase
{
    private readonly ISessionTracker _sessions;
    private readonly ISecurityOverviewService _overview;
    private readonly ICurrentUser _currentUser;

    /// <summary>Initialises a new instance.</summary>
    public AccountSessionsController(
        ISessionTracker sessions,
        ISecurityOverviewService overview,
        ICurrentUser currentUser)
    {
        _sessions = sessions;
        _overview = overview;
        _currentUser = currentUser;
    }

    /// <summary>Tells the server this session is still in use.</summary>
    /// <remarks>
    /// Call about once a minute while the app is open. It is what makes "last active 2 minutes ago"
    /// true, and a session that stops calling is shown idle within a few minutes. A 401 here, with
    /// <c>X-Session-Revoked: true</c>, means the session was ended elsewhere - sign the person out
    /// and say why, rather than trying to refresh.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">Recorded.</response>
    /// <response code="401">The session has been ended.</response>
    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId || _currentUser.SessionId is not { } sessionId)
        {
            return Unauthorized();
        }

        if (!await _sessions.TouchAsync(userId, sessionId, cancellationToken))
        {
            Response.Headers[AppConstants.Headers.SessionRevoked] = "true";

            return Unauthorized();
        }

        return NoContent();
    }

    /// <summary>Lists the devices the signed-in person has used recently.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Their devices, this one first.</response>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeviceResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMySessionsAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        return Success(await _overview.GetDevicesAsync(null, userId, _currentUser.SessionId, cancellationToken));
    }

    /// <summary>Ends one of the signed-in person's own sessions.</summary>
    /// <remarks>What the "This wasn't me" link in a new-sign-in alert leads to.</remarks>
    /// <param name="sessionId">Public session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Ended.</response>
    /// <response code="404">Not one of theirs.</response>
    [HttpPost("sessions/{sessionId}/revoke")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeMySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        await _overview.RevokeAsync(sessionId, null, userId, userId, cancellationToken);

        return SuccessEmpty("Session ended.");
    }
}

/// <summary>A workspace administrator's view of their own people's sessions.</summary>
/// <remarks>
/// Shows devices, never risk. An administrator deciding whether a colleague's four devices are a
/// phone, a laptop and two office desktops has the context to judge; a score saying "suspected
/// sharing" would prejudge it.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/security")]
[Authorize]
public sealed class WorkspaceSecurityController : ApiControllerBase
{
    private readonly ISecurityOverviewService _overview;
    private readonly ITenantScopeResolver _scope;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;

    /// <summary>Initialises a new instance.</summary>
    public WorkspaceSecurityController(
        ISecurityOverviewService overview,
        ITenantScopeResolver scope,
        ITenantContext tenantContext,
        ICurrentUser currentUser)
    {
        _overview = overview;
        _scope = scope;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
    }

    /// <summary>Seats against sessions and devices for the workspace.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The workspace's security summary, without risk scores.</response>
    [HttpGet]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<OrganizationSecurityResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync([FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _overview.GetOrganizationAsync(
            _tenantContext.RequireTenantId(),
            includeRisk: false,
            cancellationToken));
    }

    /// <summary>One person's devices.</summary>
    /// <param name="employeeId">Public employee identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Their devices.</response>
    /// <response code="404">Not a member of this workspace.</response>
    [HttpGet("employees/{employeeId}/devices")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeviceResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDevicesAsync(
        string employeeId,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var userId = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        return Success(await _overview.GetDevicesAsync(
            _tenantContext.RequireTenantId(),
            userId,
            _currentUser.SessionId,
            cancellationToken));
    }

    /// <summary>Ends one of a colleague's sessions.</summary>
    /// <param name="sessionId">Public session identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Ended; that device is signed out on its next request.</response>
    /// <response code="404">Not a session in this workspace.</response>
    [HttpPost("sessions/{sessionId}/revoke")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(
        string sessionId,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var revokedBy = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        await _overview.RevokeAsync(sessionId, _tenantContext.RequireTenantId(), null, revokedBy, cancellationToken);

        return SuccessEmpty("Session ended.");
    }
}

/// <summary>Platform staff's view of any workspace's sessions, with risk.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/superadmin/security")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class SuperAdminSecurityController : ApiControllerBase
{
    private readonly ISecurityOverviewService _overview;
    private readonly ICurrentUser _currentUser;

    /// <summary>Initialises a new instance.</summary>
    public SuperAdminSecurityController(ISecurityOverviewService overview, ICurrentUser currentUser)
    {
        _overview = overview;
        _currentUser = currentUser;
    }

    /// <summary>A workspace's seats, sessions, devices and each person's risk.</summary>
    /// <param name="tenantId">Public tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The summary, riskiest people first.</response>
    /// <response code="404">No such workspace.</response>
    [HttpGet("tenants/{tenantId}")]
    [ProducesResponseType(typeof(ApiResponse<OrganizationSecurityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizationAsync(string tenantId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Tenant, tenantId, "tenant");

        return Success(await _overview.GetOrganizationAsync(id, includeRisk: true, cancellationToken));
    }

    /// <summary>One person's devices in a workspace.</summary>
    /// <param name="tenantId">Public tenant identifier.</param>
    /// <param name="employeeId">Public employee identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Their devices.</response>
    /// <response code="404">Not a member of that workspace.</response>
    [HttpGet("tenants/{tenantId}/employees/{employeeId}/devices")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeviceResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDevicesAsync(
        string tenantId,
        string employeeId,
        CancellationToken cancellationToken)
    {
        var tenant = PublicId.Parse(PublicId.Tenant, tenantId, "tenant");
        var userId = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        return Success(await _overview.GetDevicesAsync(tenant, userId, null, cancellationToken));
    }

    /// <summary>Ends any session on the platform.</summary>
    /// <param name="sessionId">Public session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Ended; that device is signed out on its next request.</response>
    /// <response code="404">No such session.</response>
    [HttpPost("sessions/{sessionId}/revoke")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(string sessionId, CancellationToken cancellationToken)
    {
        var revokedBy = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        await _overview.RevokeAsync(sessionId, null, null, revokedBy, cancellationToken);

        return SuccessEmpty("Session ended.");
    }
}
