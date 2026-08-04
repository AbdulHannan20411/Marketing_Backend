namespace Marketing.DataAccess.Entities;

/// <summary>
/// A named set of permissions. Platform-level data shared by every tenant, so not
/// <see cref="ITenantScoped"/> - tenants assign roles, they do not define them.
/// </summary>
public sealed class Role : BaseEntity
{
    /// <summary>Role name as declared in <c>RoleNames</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Uppercased name used for lookup and uniqueness.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>Operator-facing description shown on the role management screen.</summary>
    public required string Description { get; set; }

    /// <summary>
    /// Whether the role ships with the platform. System roles cannot be renamed or deleted, which
    /// keeps the strings embedded in JWTs and authorization policies stable.
    /// </summary>
    public bool IsSystemRole { get; set; }

    /// <summary>
    /// Permissions granted by this role, stored as a text array. A join table would be more
    /// normalised, but permissions are read on every token issuance and never queried
    /// independently, so keeping them inline avoids a join on the hottest path in the system.
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Assignments of this role to users.</summary>
    public ICollection<UserRole> UserRoles { get; set; } = [];
}
