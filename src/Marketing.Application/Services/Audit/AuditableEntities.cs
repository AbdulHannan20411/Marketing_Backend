using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services.Audit;

/// <summary>One record type whose history may be read, and by whom.</summary>
/// <param name="PublicName">What the client asks for: <c>Template</c>.</param>
/// <param name="EntityName">What <c>AuditLog.EntityName</c> holds: <c>MessageTemplate</c>.</param>
/// <param name="IdPrefix">The public id prefix, so <c>tpl_18</c> parses to a row id.</param>
/// <param name="Permission">
/// Permission needed to read the underlying record. Null means any signed-in member of the
/// workspace, which is only right for records everyone can already see.
/// </param>
/// <param name="PlatformOnly">Readable by platform staff alone - plans, tenants and the like.</param>
/// <param name="SingleRowPerTenant">
/// True for a record a workspace has exactly one of - its auto-reply settings, its own profile.
/// The client asks for those as <c>current</c>, which is resolved to the workspace's row, because
/// a public id for a row there can only be one of tells nobody anything.
/// </param>
/// <param name="KeyColumn">
/// Column holding the business key, for records the rest of the API addresses by name rather than
/// by id - the email templates are <c>/email-templates/{key}</c> everywhere else, so history is
/// asked for the same way rather than by a number the client has never seen.
/// </param>
public sealed record AuditableEntity(
    string PublicName,
    string EntityName,
    string IdPrefix,
    string? Permission,
    bool PlatformOnly = false,
    bool SingleRowPerTenant = false,
    string? KeyColumn = null);

/// <summary>The identifier a caller may use in place of a public id for a single-row record.</summary>
public static class AuditableRecord
{
    /// <summary>"This workspace's row", for records a workspace has exactly one of.</summary>
    public const string Current = "current";
}

/// <summary>
/// Which records have a history, keyed by the name the client asks with.
/// </summary>
/// <remarks>
/// The registry is what makes this feature reusable: the interceptor already audits every
/// <c>BaseEntity</c>, so switching history on for another record type is one line here rather than
/// an endpoint, a DTO and a permission check of its own.
/// <para>
/// A name that is not listed is a 404 rather than an empty page. A typo in a client template
/// should be loud; "no history" is a different answer and would hide the mistake for months.
/// </para>
/// </remarks>
public static class AuditableEntities
{
    private static readonly AuditableEntity[] Registered =
    [
        new("Template", nameof(MessageTemplate), PublicId.Template, Permissions.WhatsApp.TemplatesView),
        new("Employee", nameof(User), PublicId.Employee, Permissions.Settings.Employees),
        new("Contact", nameof(Contact), PublicId.Contact, Permissions.Contacts.View),
        new("Campaign", nameof(Campaign), PublicId.Campaign, Permissions.WhatsApp.CampaignsReports),
        new("Group", nameof(ContactGroup), PublicId.Group, Permissions.Contacts.GroupsManage),
        new("Tag", nameof(ContactTag), PublicId.Tag, Permissions.Contacts.TagsManage),

        // One settings row per workspace, asked for as "current". What a business says to its
        // customers unattended is the first thing anyone asks about after a bad automatic reply.
        new(
            "AutoReplySettings",
            nameof(AutoReplySettings),
            IdPrefix: string.Empty,
            Permissions.Ai.AutoReplyManage,
            SingleRowPerTenant: true),

        // Renames, default changes, disconnections - and the per-employee access rows that hang
        // off the same number.
        new("WhatsAppAccount", nameof(WhatsAppConnection), PublicId.WhatsAppAccount, Permissions.WhatsApp.Connect),

        // Addressed by key, as /superadmin/email-templates/{key} already is. Bodies are redacted,
        // which is the point: who changed the mail every workspace receives, and when.
        new(
            "EmailTemplate",
            nameof(EmailTemplate),
            IdPrefix: string.Empty,
            Permissions.Platform.Tenants,
            PlatformOnly: true,
            KeyColumn: "key"),

        new("Plan", nameof(SubscriptionPlan), PublicId.Plan, Permissions.Platform.Plans, PlatformOnly: true),

        // The workspace's own profile. Accepts "current" as well as its id, since a member reading
        // their own workspace's history has no reason to name it.
        new(
            "Workspace",
            nameof(Tenant),
            PublicId.Tenant,
            Permissions.Settings.Company,
            SingleRowPerTenant: true),

        // Add a line to enable history for another record. Nothing else changes.
    ];

    /// <summary>Every registered record type, for diagnostics and tests.</summary>
    public static IReadOnlyList<AuditableEntity> All => Registered;

    /// <summary>The record type a public name refers to, or null when it is not registered.</summary>
    /// <param name="publicName">Name the client sent, matched case-insensitively.</param>
    public static AuditableEntity? Find(string? publicName) =>
        publicName is { Length: > 0 }
            ? Array.Find(
                Registered,
                entry => string.Equals(entry.PublicName, publicName, StringComparison.OrdinalIgnoreCase))
            : null;
}
