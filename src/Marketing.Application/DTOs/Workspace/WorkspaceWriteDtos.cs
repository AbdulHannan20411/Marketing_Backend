using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Workspace;

/// <summary>Request to invite an employee.</summary>
/// <param name="Email">Address the invitation goes to.</param>
/// <param name="Name">Full name.</param>
/// <param name="JobTitle">Job title.</param>
/// <param name="Permissions">
/// Starting grant. Anything beyond the Employee role's defaults is stored as a per-user override.
/// </param>
public sealed record InviteEmployeeRequest(
    string Email,
    string Name,
    string JobTitle,
    IReadOnlyList<string>? Permissions = null);

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
