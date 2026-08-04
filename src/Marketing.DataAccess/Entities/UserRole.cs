namespace Marketing.DataAccess.Entities;

/// <summary>
/// Assignment of a <see cref="Role"/> to a <see cref="User"/>.
/// <para>
/// Modelled as an entity rather than a shadow join table because the assignment itself is
/// auditable: who granted an operator tenant-owner rights, and when, is a question compliance
/// reviews actually ask.
/// </para>
/// </summary>
public sealed class UserRole : BaseEntity, ITenantScoped
{
    /// <summary>User receiving the role.</summary>
    public Guid UserId { get; set; }

    /// <summary>Role being granted.</summary>
    public Guid RoleId { get; set; }

    /// <summary>User navigation.</summary>
    public User User { get; set; } = null!;

    /// <summary>Role navigation.</summary>
    public Role Role { get; set; } = null!;
}
