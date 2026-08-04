namespace Marketing.Common.Constants;

/// <summary>
/// Authorization policy names registered in the API host.
/// Controllers reference these constants rather than raw role strings so that the mapping from
/// intent ("may administer the platform") to roles stays in exactly one place.
/// </summary>
public static class PolicyNames
{
    /// <summary>Requires the <see cref="RoleNames.PlatformAdmin"/> role.</summary>
    public const string PlatformAdministration = "policy:platform-administration";

    /// <summary>Requires ownership of the current tenant, or platform administration.</summary>
    public const string TenantAdministration = "policy:tenant-administration";

    /// <summary>Requires any authenticated member of the current tenant.</summary>
    public const string TenantMembership = "policy:tenant-membership";

    /// <summary>Requires a resolved, non-empty tenant on the request.</summary>
    public const string RequireTenant = "policy:require-tenant";
}
