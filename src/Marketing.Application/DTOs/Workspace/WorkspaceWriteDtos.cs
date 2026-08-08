using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Workspace;

/// <summary>Request to invite an employee.</summary>
/// <param name="Email">Address the invitation goes to.</param>
/// <param name="Name">Full name.</param>
/// <param name="JobTitle">Job title.</param>
/// <param name="Permissions">
/// Starting grant. Anything beyond the Employee role's defaults is stored as a per-user override.
/// </param>
/// <param name="Role">
/// Role to grant. Defaults to Employee; only an Admin may grant Admin, and Super Admin is never
/// assignable here.
/// </param>
/// <param name="PermissionSetId">
/// A saved set to start from, as an alternative to listing permissions. Ignored when
/// <paramref name="Permissions"/> is supplied, because an explicit list is the more specific
/// instruction.
/// </param>
public sealed record InviteEmployeeRequest(
    string Email,
    string Name,
    string JobTitle,
    IReadOnlyList<string>? Permissions = null,
    string? Role = null,
    string? PermissionSetId = null);

/// <summary>Request to change an employee's role.</summary>
/// <param name="Role">New role: <c>Admin</c> or <c>Employee</c>.</param>
public sealed record UpdateEmployeeRoleRequest(string Role);

/// <summary>Request to change an employee's profile fields. Omitted fields are left alone.</summary>
/// <param name="Name">Full name.</param>
/// <param name="JobTitle">Job title.</param>
/// <param name="Email">Address, which must not already belong to someone else.</param>
public sealed record UpdateEmployeeRequest(
    string? Name = null,
    string? JobTitle = null,
    string? Email = null);

/// <summary>Request to apply a saved permission set to several employees.</summary>
/// <param name="EmployeeIds">Employees to overwrite.</param>
public sealed record ApplyPermissionSetRequest(IReadOnlyList<string> EmployeeIds);

/// <summary>Request to replace an employee's effective permissions.</summary>
/// <param name="Permissions">
/// The complete effective set the employee should end up with. The difference against their role
/// defaults is stored as grant and revoke overrides.
/// </param>
public sealed record UpdatePermissionsRequest(IReadOnlyList<string> Permissions);

/// <summary>Request to change an employee's account state.</summary>
/// <param name="Status">New state.</param>
public sealed record UpdateEmployeeStatusRequest(EmployeeStatus Status);

/// <summary>Request to create or replace a permission set.</summary>
/// <param name="Name">Set name.</param>
/// <param name="Description">Description.</param>
/// <param name="Permissions">Permissions in the set.</param>
public sealed record PermissionSetDraft(
    string Name,
    string Description,
    IReadOnlyList<string> Permissions);
