using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>A reusable named bundle of permissions an Admin can apply to employees.</summary>
public sealed class PermissionSet : BaseEntity, IRequiresTenant
{
    /// <summary>Set name.</summary>
    public required string Name { get; set; }

    /// <summary>Operator-facing description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Whether the platform ships this set. System sets cannot be deleted, so a tenant cannot
    /// remove the baseline grant every new employee starts from.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>Permissions in the set, stored as a text array.</summary>
    public List<string> Permissions { get; set; } = [];
}

/// <summary>
/// A per-user adjustment to the permissions their role grants.
/// <para>
/// The client is told an employee's <em>effective</em> grant and never derives it from role, so
/// the server has to be able to express "this person, unlike others with the same role, may also
/// export contacts". Modelled as explicit grant and revoke deltas rather than a replacement list:
/// a replacement would silently freeze that user's permissions the next time the role's defaults
/// change.
/// </para>
/// </summary>
public sealed class UserPermissionOverride : BaseEntity, IRequiresTenant
{
    /// <summary>User the override applies to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Permission from the catalogue.</summary>
    public required string Permission { get; set; }

    /// <summary>
    /// True to add the permission on top of the role grant, false to take it away.
    /// A revoke always beats a grant when both somehow exist.
    /// </summary>
    public bool IsGranted { get; set; }

    /// <summary>User navigation.</summary>
    public User User { get; set; } = null!;
}

/// <summary>A message shown in the notification centre.</summary>
public sealed class Notification : BaseEntity, IRequiresTenant
{
    /// <summary>
    /// Recipient. Null means every member of the tenant sees it, which is how platform-wide
    /// warnings such as an expiring subscription are delivered without fanning out a row per user.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>What the notification is about.</summary>
    public NotificationKind Kind { get; set; }

    /// <summary>Headline.</summary>
    public required string Title { get; set; }

    /// <summary>Body text.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Severity.</summary>
    public NotificationPriority Priority { get; set; } = NotificationPriority.Info;

    /// <summary>
    /// Icon key from the client's registry. An unrecognised key renders nothing, so these are
    /// chosen from the registry rather than invented.
    /// </summary>
    public string Icon { get; set; } = "bell";

    /// <summary>Whether the recipient has read it.</summary>
    public bool Read { get; set; }

    /// <summary>Call-to-action label, when there is one.</summary>
    public string? ActionLabel { get; set; }

    /// <summary>In-app route the action navigates to, for example <c>/billing</c>.</summary>
    public string? ActionRoute { get; set; }

    /// <summary>Instant the event happened.</summary>
    public DateTimeOffset OccurredOn { get; set; }
}
