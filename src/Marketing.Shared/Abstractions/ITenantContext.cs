namespace Marketing.Shared.Abstractions;

/// <summary>
/// Resolves the tenant that owns the current unit of work.
/// <para>
/// This is the <em>only</em> supported source of a tenant identifier in the entire backend. It is
/// populated from the validated <c>tenant_id</c> JWT claim (or, for background jobs, from an
/// explicit ambient scope). Nothing reads a tenant from a route, query string, header or body.
/// </para>
/// </summary>
public interface ITenantContext
{
    /// <summary>Current tenant, or <see langword="null"/> for anonymous and platform-level work.</summary>
    public long? TenantId { get; }

    /// <summary>Slug of the current tenant, carried for logging only.</summary>
    public string? TenantSlug { get; }

    /// <summary>Whether a tenant has been resolved.</summary>
    public bool HasTenant { get; }

    /// <summary>
    /// Whether the principal may read across tenants. When true the global query filter is
    /// bypassed, so this is granted to platform administrators only.
    /// </summary>
    public bool CanAccessAllTenants { get; }

    /// <summary>
    /// Returns the current tenant or throws.
    /// Use this in service code whenever a missing tenant is a bug rather than a valid state -
    /// it converts a silent cross-tenant query into a loud failure.
    /// </summary>
    /// <exception cref="Common.Exceptions.TenantResolutionException">No tenant is resolved.</exception>
    public long RequireTenantId();

    /// <summary>
    /// Runs work under an explicit tenant, restoring the previous ambient value on dispose.
    /// <para>
    /// Intended for Quartz jobs and webhook processing, where there is no bearer token but the
    /// tenant is known from the job payload or the resolved WhatsApp phone number.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant to enter.</param>
    /// <param name="tenantSlug">Optional slug for log context.</param>
    /// <returns>A scope that restores the previous tenant when disposed.</returns>
    public IDisposable BeginScope(long tenantId, string? tenantSlug = null);
}
