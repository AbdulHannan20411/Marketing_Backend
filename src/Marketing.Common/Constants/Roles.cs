namespace Marketing.Common.Constants;

/// <summary>
/// The platform's role catalogue.
/// <para>
/// These strings are persisted in the <c>roles</c> table and embedded in JWT role claims, so they
/// are a versioned contract: never rename an existing value, only add new ones. A rename would
/// invalidate every token already in circulation and orphan every stored assignment.
/// </para>
/// </summary>
public static class Roles
{
    /// <summary>
    /// Operates the platform itself and may act across every tenant.
    /// <para>
    /// The only role that bypasses the tenant query filter. Grant it sparingly - it is the one
    /// role for which the multi-tenant isolation guarantees do not apply.
    /// </para>
    /// </summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>
    /// Administers a single tenant: billing, users, WhatsApp connection and all tenant data.
    /// Scoped strictly to their own tenant.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Day-to-day operator inside a single tenant - contacts, templates, campaigns, reports.
    /// Cannot manage users, billing or the WhatsApp connection.
    /// </summary>
    public const string Employee = "Employee";

    /// <summary>Every role, ordered from most to least privileged.</summary>
    public static readonly IReadOnlyList<string> All = [SuperAdmin, Admin, Employee];

    /// <summary>
    /// Roles that belong to a tenant and therefore require a tenant claim on their token.
    /// <see cref="SuperAdmin"/> is deliberately excluded: platform staff exist outside every tenant.
    /// </summary>
    public static readonly IReadOnlyList<string> TenantScoped = [Admin, Employee];

    /// <summary>Roles permitted to administer a tenant.</summary>
    public static readonly IReadOnlyList<string> TenantAdministrators = [SuperAdmin, Admin];

    /// <summary>Returns whether a role name is one the platform recognises.</summary>
    /// <param name="role">Candidate role name.</param>
    public static bool IsKnown(string? role) =>
        role is not null && All.Contains(role, StringComparer.Ordinal);

    /// <summary>Returns whether a role is scoped to a single tenant.</summary>
    /// <param name="role">Role name.</param>
    public static bool IsTenantScoped(string? role) =>
        role is not null && TenantScoped.Contains(role, StringComparer.Ordinal);

    /// <summary>
    /// Normalised form used for the unique index and lookups on the <c>roles</c> table.
    /// </summary>
    /// <param name="role">Role name.</param>
    public static string Normalise(string role) => role.ToUpperInvariant();
}
