using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>Employees of the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employees")]
[Authorize]
[RequireModule(PlanModules.Employees)]
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

    /// <summary>Returns every employee with their effective permissions.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The employees.</response>
    [HttpGet]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmployeeResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync([FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.GetEmployeesAsync(cancellationToken));
    }

    /// <summary>Invites an employee.</summary>
    /// <remarks>Refused with a 422 naming the limit when the plan's seats are all in use.</remarks>
    /// <response code="200">The invited employee.</response>
    /// <response code="422">The plan's seat allowance is exhausted.</response>
    [HttpPost("invite")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> InviteAsync(
        [FromBody] InviteEmployeeRequest request,
        CancellationToken cancellationToken)
    {
        var employee = await _employees.InviteAsync(request, cancellationToken);

        return Success(employee, $"Invitation sent to {employee.Email}.");
    }

    /// <summary>Replaces an employee's effective permissions.</summary>
    /// <remarks>
    /// Rotates the employee's security stamp, so the change takes effect at their next token
    /// refresh rather than whenever their current access token happens to expire.
    /// </remarks>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">The complete effective set they should end up with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    [HttpPut("{id}/permissions")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePermissionsAsync(
        string id,
        [FromBody] UpdatePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var employee = await _employees.UpdatePermissionsAsync(id, request, cancellationToken);

        return Success(employee, $"Permissions updated for {employee.Name}.");
    }

    /// <summary>Changes an employee's account state.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">New state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    [HttpPut("{id}/status")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateStatusAsync(
        string id,
        [FromBody] UpdateEmployeeStatusRequest request,
        CancellationToken cancellationToken)
    {
        var employee = await _employees.UpdateStatusAsync(id, request, cancellationToken);

        return Success(employee, $"{employee.Name} is now {employee.Status}.");
    }

    /// <summary>Changes an employee's role.</summary>
    /// <remarks>
    /// Only an Admin may grant the Admin role, Super Admin is never assignable here, and nobody may
    /// change their own role or demote the last remaining Admin.
    /// </remarks>
    /// <param name="id">Employee identifier.</param>
    /// <param name="request">New role.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated employee.</response>
    /// <response code="403">The caller may not grant that role.</response>
    [HttpPut("{id}/role")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<EmployeeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateRoleAsync(
        string id,
        [FromBody] UpdateEmployeeRoleRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var employee = await _employees.UpdateRoleAsync(id, request, cancellationToken);

        return Success(employee, $"{employee.Name} is now {employee.Role}.");
    }

    /// <summary>Updates an employee's name, job title or email.</summary>
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

    /// <summary>Sends a fresh invitation, invalidating any outstanding one.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The invitation was sent again.</response>
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

        return SuccessEmpty("Invitation sent again.");
    }

    /// <summary>Withdraws a pending invitation and frees the seat.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The invitation was withdrawn.</response>
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

        return SuccessEmpty("Invitation withdrawn.");
    }

    /// <summary>Removes an employee and ends their sessions.</summary>
    /// <param name="id">Employee identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The employee was removed.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _employees.DeleteAsync(id, cancellationToken);

        return SuccessEmpty("Employee removed.");
    }
}

/// <summary>Reusable permission bundles.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/permission-sets")]
[Authorize]
[RequireModule(PlanModules.Employees)]
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
    public async Task<IActionResult> GetAsync([FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _employees.GetPermissionSetsAsync(cancellationToken));
    }

    /// <summary>Creates a permission set.</summary>
    /// <response code="200">The created set.</response>
    [HttpPost]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<PermissionSetResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] PermissionSetDraft draft,
        CancellationToken cancellationToken)
    {
        var set = await _employees.CreatePermissionSetAsync(draft, cancellationToken);

        return Success(set, $"Permission set \"{set.Name}\" created.");
    }

    /// <summary>Replaces a permission set.</summary>
    /// <param name="id">Permission set identifier.</param>
    /// <param name="draft">Replacement content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated set.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<PermissionSetResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] PermissionSetDraft draft,
        CancellationToken cancellationToken)
    {
        var set = await _employees.UpdatePermissionSetAsync(id, draft, cancellationToken);

        return Success(set, $"Permission set \"{set.Name}\" saved.");
    }

    /// <summary>Deletes a permission set. System sets cannot be deleted.</summary>
    /// <param name="id">Permission set identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The set was deleted.</response>
    /// <response code="409">The set ships with the platform.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Settings.Employees)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _employees.DeletePermissionSetAsync(id, cancellationToken);

        return SuccessEmpty("Permission set deleted.");
    }

    /// <summary>Applies a saved set to several employees, overwriting their permissions.</summary>
    /// <remarks>
    /// Subject to the same guards as editing permissions directly, so a saved set cannot become a
    /// way to grant something the caller could not grant by hand.
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

        var employees = await _employees.ApplyPermissionSetAsync(id, request, cancellationToken);

        return Success(employees, $"Applied to {employees.Count} people.");
    }
}

/// <summary>Notifications for the signed-in user.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
[Authorize]
public sealed class NotificationsController : ApiControllerBase
{
    private readonly INotificationService _notifications;

    /// <summary>Initialises a new instance.</summary>
    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    /// <summary>Returns the caller's notifications, newest first.</summary>
    /// <response code="200">The notifications.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken) =>
        Success(await _notifications.GetAsync(cancellationToken));

    /// <summary>Marks one notification read.</summary>
    /// <remarks>Returns the full updated list so the client replaces its state in one step.</remarks>
    /// <param name="id">Notification identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated list.</response>
    [HttpPost("{id}/read")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkReadAsync(string id, CancellationToken cancellationToken) =>
        Success(await _notifications.MarkReadAsync(id, cancellationToken));

    /// <summary>Marks every notification read.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated list.</response>
    [HttpPost("read-all")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllReadAsync(CancellationToken cancellationToken) =>
        Success(await _notifications.MarkAllReadAsync(cancellationToken));
}

/// <summary>Global search across the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/search")]
[Authorize]
public sealed class SearchController : ApiControllerBase
{
    private readonly ISearchService _search;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public SearchController(ISearchService search, ITenantScopeResolver scope)
    {
        _search = search;
        _scope = scope;
    }

    /// <summary>Searches records and destinations, respecting the caller's permissions.</summary>
    /// <remarks>
    /// Groups are returned in a fixed order, each capped at four, and empty groups are omitted.
    /// A blank term returns an empty array.
    /// </remarks>
    /// <param name="q">Search term.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The result groups.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SearchResultGroup>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _search.SearchAsync(q, cancellationToken));
    }
}
