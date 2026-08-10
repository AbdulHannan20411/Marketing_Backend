using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Platform;

/// <summary>
/// Request to create an Admin account.
/// <para>
/// Creates the organisation and its first administrator together, because an Admin without a
/// tenant cannot do anything and a tenant without an Admin cannot be reached.
/// </para>
/// </summary>
/// <param name="Name">Full name of the account owner.</param>
/// <param name="Email">Owner's email address.</param>
/// <param name="Organisation">Organisation name.</param>
/// /// <param name="Password">Password name.</param>
/// <param name="Plan">Commercial plan band.</param>
public sealed record CreateAdminAccountRequest(
    string Name,
    string Email,
    string Organisation,
    string Password,
    TenantPlan Plan = TenantPlan.Starter);

/// <summary>Request to update an Admin account. Omitted fields are left unchanged.</summary>
/// <param name="Name">Owner's full name.</param>
/// <param name="Organisation">Organisation name.</param>
/// <param name="Plan">Commercial plan band.</param>
public sealed record UpdateAdminAccountRequest(
    string? Name = null,
    string? Organisation = null,
    TenantPlan? Plan = null);

/// <summary>Request to suspend or reactivate an Admin account.</summary>
/// <param name="Status">New state.</param>
public sealed record UpdateAdminStatusRequest(TenantAccountStatus Status);
