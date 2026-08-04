namespace Marketing.Common.Constants;

/// <summary>
/// Custom JWT claim types issued by this platform.
/// <para>
/// The tenant identifier is transported here and nowhere else. It is never accepted from a
/// request body, route value, query string or header - see the security notes in CLAUDE.md.
/// </para>
/// </summary>
public static class ApplicationClaimTypes
{
    /// <summary>Tenant the token was issued for. Absent for platform administrators.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Human-readable tenant slug, carried for logging and diagnostics only.</summary>
    public const string TenantSlug = "tenant_slug";

    /// <summary>Opaque identifier of the session that issued the token.</summary>
    public const string SessionId = "sid";

    /// <summary>Fine-grained permission granted to the principal.</summary>
    public const string Permission = "perm";

    /// <summary>Full name of the authenticated user.</summary>
    public const string DisplayName = "display_name";
}
