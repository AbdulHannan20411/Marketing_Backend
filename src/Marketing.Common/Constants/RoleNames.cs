namespace Marketing.Common.Constants;

/// <summary>
/// Canonical role names. These strings are persisted in the database and embedded in JWT claims,
/// so they are a versioned contract: never rename an existing value, only add new ones.
/// </summary>
public static class RoleNames
{
    /// <summary>Operates the platform itself and may act across every tenant.</summary>
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>Owns a single tenant: billing, users, WhatsApp connection, all tenant data.</summary>
    public const string TenantOwner = "TenantOwner";

    /// <summary>Day-to-day operator inside a single tenant.</summary>
    public const string TenantUser = "TenantUser";

    /// <summary>All roles, ordered from most to least privileged.</summary>
    public static readonly IReadOnlyList<string> All = [PlatformAdmin, TenantOwner, TenantUser];

    /// <summary>Roles that are scoped to a tenant and therefore require a tenant claim.</summary>
    public static readonly IReadOnlyList<string> TenantScoped = [TenantOwner, TenantUser];
}
