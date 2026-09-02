using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>
/// Employees of the resolved tenant: invitations, roles, permissions and access.
/// </summary>
/// <remarks>
/// Every route is behind <see cref="Permissions.Settings.Employees"/> and scoped to the tenant on
/// the token. No route accepts a tenant identifier - one in the path would let an administrator
/// manage another workspace's people by guessing.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employees")]
[Authorize]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class EmployeesController : ApiControllerBase
{
    private readonly IEmployeeService _employees;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public EmployeesController(IEmployeeService employees, ITenantScopeResolver scope)
    {
        _employees = employees;
        _scope = scope;
    }

    /// <summary>Returns everyone in the workspace with their effective permissions.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The employees.</response>
    [HttpGet]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmployeeResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.GetEmployeesAsync(cancellationToken));
    }

    /// <summary>Invites an employee.</summary>
    /// <remarks>
    /// Refused with <c>409 seat_limit_reached</c> when the plan's seat allowance is already used.
    /// The invitation email is sent attributed to the inviter; the account exists in a pending
    /// state until the invitation is redeemed.
    /// </remarks>
    /// <param name="request">Who to invite, and what they may do.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The invited employee.</response>
    /// <response code="409">The plan has no seats left, or the address is already in use.</response>
    [HttpPost("invite")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> InviteAsync(
        [FromBody] InviteEmployeeRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var employee = await _employees.InviteAsync(request, cancellationToken);

        return Success(employee, $"Invitation sent to {employee.Email}.");
    }

    /// <summary>Changes an employee's name, job title or email. Omitted fields are left alone.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">Fields to change.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] UpdateEmployeeRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.UpdateAsync(id, request, cancellationToken), "Employee saved.");
    }

    /// <summary>Replaces an employee's effective permissions.</summary>
    /// <remarks>
    /// A complete replacement set, not a delta. Their security stamp is rotated, so the change
    /// takes effect at their next refresh rather than whenever their current token happens to lapse.
    /// </remarks>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">The complete set of permissions they should hold.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    [HttpPut("{id}/permissions")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePermissionsAsync(
        string id,
        [FromBody] UpdatePermissionsRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _employees.UpdatePermissionsAsync(id, request, cancellationToken),
            "Permissions updated.");
    }

    /// <summary>Changes an employee's role.</summary>
    /// <remarks>
    /// Their security stamp is rotated, so the new role applies on their next request rather than
    /// when their current token expires - which matters most when a role is being taken away.
    /// </remarks>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">The new role.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    [HttpPut("{id}/role")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateRoleAsync(
        string id,
        [FromBody] UpdateEmployeeRoleRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.UpdateRoleAsync(id, request, cancellationToken), "Role updated.");
    }

    /// <summary>Suspends or reinstates an employee.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    [HttpPut("{id}/status")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateStatusAsync(
        string id,
        [FromBody] UpdateEmployeeStatusRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.UpdateStatusAsync(id, request, cancellationToken), "Employee updated.");
    }

    /// <summary>Issues a fresh invitation, invalidating any outstanding one.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The invitation was resent.</response>
    [HttpPost("{id}/resend-invite")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendInviteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _employees.ResendInviteAsync(id, cancellationToken);

        return SuccessEmpty("Invitation resent.");
    }

    /// <summary>Withdraws a pending invitation and removes the account it was for.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The invitation was revoked.</response>
    [HttpDelete("{id}/invite")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeInviteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _employees.RevokeInviteAsync(id, cancellationToken);

        return SuccessEmpty("Invitation revoked.");
    }

    /// <summary>Removes an employee, ends their sessions and frees their seat.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The employee was removed.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _employees.DeleteAsync(id, cancellationToken);

        return SuccessEmpty("Employee removed.");
    }
}

/// <summary>
/// Saved permission sets, for granting the same access to several people at once.
/// </summary>
/// <remarks>
/// A separate controller because the resource is separate: a permission set belongs to the
/// workspace rather than to any one employee, and lives at its own path.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/permission-sets")]
[Authorize]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class PermissionSetsController : ApiControllerBase
{
    private readonly IEmployeeService _employees;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public PermissionSetsController(IEmployeeService employees, ITenantScopeResolver scope)
    {
        _employees = employees;
        _scope = scope;
    }

    /// <summary>Returns every permission set.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The permission sets.</response>
    [HttpGet]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionSetResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.GetPermissionSetsAsync(cancellationToken));
    }

    /// <summary>Creates a permission set.</summary>
    /// <param name="draft">Name, description and permissions.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The created set.</response>
    [HttpPost]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<PermissionSetResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] PermissionSetDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var set = await _employees.CreatePermissionSetAsync(draft, cancellationToken);

        return Success(set, $"Permission set \"{set.Name}\" created.");
    }

    /// <summary>Replaces a permission set.</summary>
    /// <param name="id">Permission set identifier.</param>
    /// <param name="draft">The replacement.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated set.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<PermissionSetResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] PermissionSetDraft draft,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(
            await _employees.UpdatePermissionSetAsync(id, draft, cancellationToken),
            "Permission set saved.");
    }

    /// <summary>Deletes a permission set.</summary>
    /// <remarks>System sets cannot be deleted; the service refuses with a 409.</remarks>
    /// <param name="id">Permission set identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The set was deleted.</response>
    /// <response code="409">The set is a system set and cannot be deleted.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _employees.DeletePermissionSetAsync(id, cancellationToken);

        return SuccessEmpty("Permission set deleted.");
    }

    /// <summary>Overwrites several employees' permissions with a saved set.</summary>
    /// <remarks>
    /// A replacement, not a merge: each named employee ends up holding exactly the set's
    /// permissions. Returns every employee it changed, so the client can update the table without
    /// refetching.
    /// </remarks>
    /// <param name="id">Permission set identifier.</param>
    /// <param name="request">Employees to apply it to.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employees.</response>
    [HttpPost("{id}/apply")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmployeeResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ApplyAsync(
        string id,
        [FromBody] ApplyPermissionSetRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var updated = await _employees.ApplyPermissionSetAsync(id, request, cancellationToken);

        return Success(updated, $"Applied to {updated.Count} employees.");
    }
}
