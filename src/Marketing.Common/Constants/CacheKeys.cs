namespace Marketing.Common.Constants;

/// <summary>
/// Builders for every Redis key the platform writes.
/// <para>
/// Keys are always tenant-prefixed. Centralising construction here is what makes it impossible to
/// accidentally serve one tenant a cache entry populated by another, and it keeps
/// <see cref="TenantPrefix"/> available for bulk invalidation when a tenant is suspended.
/// </para>
/// </summary>
public static class CacheKeys
{
    private const string Root = "marketing";

    /// <summary>Prefix covering every entry owned by a tenant. Use with a scan-and-delete purge.</summary>
    public static string TenantPrefix(Guid tenantId) => $"{Root}:t:{tenantId:N}:";

    /// <summary>Prefix covering entries that are not tenant-scoped (platform lookups).</summary>
    public static string PlatformPrefix() => $"{Root}:platform:";

    /// <summary>Cached permission set for a user, invalidated whenever their roles change.</summary>
    public static string UserPermissions(Guid tenantId, Guid userId) =>
        $"{TenantPrefix(tenantId)}user:{userId:N}:permissions";

    /// <summary>Cached tenant record, keyed by slug for the login path.</summary>
    public static string TenantBySlug(string slug) => $"{PlatformPrefix()}tenant:slug:{slug.ToLowerInvariant()}";

    /// <summary>Cached tenant record, keyed by identifier.</summary>
    public static string TenantById(Guid tenantId) => $"{PlatformPrefix()}tenant:id:{tenantId:N}";

    /// <summary>Dashboard KPI payload for a tenant and a named period.</summary>
    public static string DashboardKpis(Guid tenantId, string period) =>
        $"{TenantPrefix(tenantId)}dashboard:kpis:{period}";

    /// <summary>Synced WhatsApp template list for a tenant.</summary>
    public static string Templates(Guid tenantId) => $"{TenantPrefix(tenantId)}templates";

    /// <summary>Tenant settings blob.</summary>
    public static string TenantSettings(Guid tenantId) => $"{TenantPrefix(tenantId)}settings";
}
