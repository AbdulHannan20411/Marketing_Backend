using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>A person a tenant can message.</summary>
public sealed class Contact : BaseEntity, IRequiresTenant
{
    /// <summary>Full name as entered.</summary>
    public required string FullName { get; set; }

    /// <summary>
    /// Phone number in E.164. Unique per tenant among live rows - the same number belonging to two
    /// contacts would make delivery receipts ambiguous and duplicate every campaign send.
    /// </summary>
    public required string PhoneNumber { get; set; }

    /// <summary>Digits only, no punctuation. Used for duplicate detection during import.</summary>
    public required string NormalizedPhoneNumber { get; set; }

    /// <summary>Email address, if known.</summary>
    public string? Email { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Marketing consent state.</summary>
    public ContactStatus Status { get; set; } = ContactStatus.Subscribed;

    /// <summary>Commercial stage, used for the lead and customer counts.</summary>
    public ContactLifecycle Lifecycle { get; set; } = ContactLifecycle.Lead;

    /// <summary>Instant marketing consent was given.</summary>
    public DateTimeOffset? OptedInAt { get; set; }

    /// <summary>Instant of the last outbound message.</summary>
    public DateTimeOffset? LastMessagedAt { get; set; }

    /// <summary>Group memberships.</summary>
    public ICollection<ContactGroupMember> GroupMemberships { get; set; } = [];

    /// <summary>Tag assignments.</summary>
    public ICollection<ContactTagAssignment> TagAssignments { get; set; } = [];
}

/// <summary>A named collection of contacts.</summary>
public sealed class ContactGroup : BaseEntity, IRequiresTenant
{
    /// <summary>Group name, unique per tenant.</summary>
    public required string Name { get; set; }

    /// <summary>Operator-facing description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Members of this group.</summary>
    public ICollection<ContactGroupMember> Members { get; set; } = [];
}

/// <summary>Membership of a contact in a group.</summary>
public sealed class ContactGroupMember : BaseEntity, IRequiresTenant
{
    /// <summary>The contact.</summary>
    public Guid ContactId { get; set; }

    /// <summary>The group.</summary>
    public Guid ContactGroupId { get; set; }

    /// <summary>Contact navigation.</summary>
    public Contact Contact { get; set; } = null!;

    /// <summary>Group navigation.</summary>
    public ContactGroup ContactGroup { get; set; } = null!;
}

/// <summary>A label that can be applied to contacts.</summary>
public sealed class ContactTag : BaseEntity, IRequiresTenant
{
    /// <summary>Tag name, unique per tenant.</summary>
    public required string Name { get; set; }

    /// <summary>Badge colour.</summary>
    public TagColor Color { get; set; } = TagColor.Neutral;

    /// <summary>Assignments of this tag.</summary>
    public ICollection<ContactTagAssignment> Assignments { get; set; } = [];
}

/// <summary>Assignment of a tag to a contact.</summary>
public sealed class ContactTagAssignment : BaseEntity, IRequiresTenant
{
    /// <summary>The contact.</summary>
    public Guid ContactId { get; set; }

    /// <summary>The tag.</summary>
    public Guid ContactTagId { get; set; }

    /// <summary>Contact navigation.</summary>
    public Contact Contact { get; set; } = null!;

    /// <summary>Tag navigation.</summary>
    public ContactTag ContactTag { get; set; } = null!;
}
