namespace Marketing.Common.Constants;

/// <summary>
/// The permission catalogue.
/// <para>
/// Roles answer "who is this person"; permissions answer "what may they do". Authorising against
/// permissions rather than role names means a customer asking for "employees who can export
/// reports but not edit contacts" is a data change, not a code change - no new role constant, no
/// new policy, no redeploy.
/// </para>
/// <para>
/// Format is <c>resource:action</c>. A grant ending in <c>:*</c> covers every action on that
/// resource, and <see cref="Platform.All"/> covers everything. Permissions are stored on the role
/// row as a text array and flattened into the token at sign-in, so a permission check is a claim
/// lookup rather than a database round trip.
/// </para>
/// </summary>
public static class Permissions
{
    /// <summary>Separator between the resource and the action.</summary>
    public const string Separator = ":";

    /// <summary>Suffix marking a grant that covers every action on a resource.</summary>
    public const string WildcardSuffix = ":*";

    /// <summary>Platform-wide grants. Reserved for <see cref="Roles.SuperAdmin"/>.</summary>
    public static class Platform
    {
        /// <summary>Unrestricted access to everything, across every tenant.</summary>
        public const string All = "platform:*";

        /// <summary>Read the tenant register.</summary>
        public const string TenantsRead = "platform:tenants:read";

        /// <summary>Create, suspend and cancel tenants.</summary>
        public const string TenantsWrite = "platform:tenants:write";

        /// <summary>Read platform-wide audit logs across tenants.</summary>
        public const string AuditRead = "platform:audit:read";

        /// <summary>Read infrastructure health and monitoring detail.</summary>
        public const string MonitoringRead = "platform:monitoring:read";

        /// <summary>Adjust tenant quotas and entitlements.</summary>
        public const string QuotasWrite = "platform:quotas:write";
    }

    /// <summary>Grants covering a tenant's own administration.</summary>
    public static class Tenant
    {
        /// <summary>Every action within the caller's own tenant.</summary>
        public const string All = "tenant:*";

        /// <summary>View tenant settings and profile.</summary>
        public const string SettingsRead = "tenant:settings:read";

        /// <summary>Change tenant settings and profile.</summary>
        public const string SettingsWrite = "tenant:settings:write";

        /// <summary>View billing and subscription detail.</summary>
        public const string BillingRead = "tenant:billing:read";

        /// <summary>Change plan and payment details.</summary>
        public const string BillingWrite = "tenant:billing:write";
    }

    /// <summary>Grants over user accounts within a tenant.</summary>
    public static class Users
    {
        /// <summary>List and view users.</summary>
        public const string Read = "users:read";

        /// <summary>Invite, edit and disable users.</summary>
        public const string Write = "users:write";

        /// <summary>Grant and revoke roles. Separated from <see cref="Write"/> because privilege
        /// escalation is a different risk from ordinary account maintenance.</summary>
        public const string ManageRoles = "users:roles:manage";
    }

    /// <summary>Grants over the contact book.</summary>
    public static class Contacts
    {
        /// <summary>List, search and view contacts.</summary>
        public const string Read = "contacts:read";

        /// <summary>Create and edit contacts.</summary>
        public const string Write = "contacts:write";

        /// <summary>Delete contacts.</summary>
        public const string Delete = "contacts:delete";

        /// <summary>Run the CSV import wizard and bulk operations.</summary>
        public const string Import = "contacts:import";

        /// <summary>Export contacts. Separated from <see cref="Read"/> because bulk extraction of
        /// personal data is the action a data-protection reviewer will ask about.</summary>
        public const string Export = "contacts:export";
    }

    /// <summary>Grants over segmentation.</summary>
    public static class Groups
    {
        /// <summary>View groups and their membership.</summary>
        public const string Read = "groups:read";

        /// <summary>Create, edit and delete groups.</summary>
        public const string Write = "groups:write";
    }

    /// <summary>Grants over tags.</summary>
    public static class Tags
    {
        /// <summary>View tags.</summary>
        public const string Read = "tags:read";

        /// <summary>Create, edit and delete tags.</summary>
        public const string Write = "tags:write";
    }

    /// <summary>Grants over the Meta WhatsApp Business Account connection.</summary>
    public static class WhatsApp
    {
        /// <summary>View connection status, phone numbers and business profile.</summary>
        public const string Read = "whatsapp:read";

        /// <summary>Run Embedded Signup, connect and disconnect the account.</summary>
        public const string Connect = "whatsapp:connect";
    }

    /// <summary>Grants over message templates.</summary>
    public static class Templates
    {
        /// <summary>View templates and their review status.</summary>
        public const string Read = "templates:read";

        /// <summary>Create, edit and submit templates for review.</summary>
        public const string Write = "templates:write";

        /// <summary>Trigger a synchronisation with Meta.</summary>
        public const string Sync = "templates:sync";
    }

    /// <summary>Grants over campaigns.</summary>
    public static class Campaigns
    {
        /// <summary>View campaigns and their progress.</summary>
        public const string Read = "campaigns:read";

        /// <summary>Create and edit campaigns.</summary>
        public const string Write = "campaigns:write";

        /// <summary>Send or schedule a campaign. Separated from <see cref="Write"/> so drafting and
        /// dispatch can be split between an operator and an approver.</summary>
        public const string Send = "campaigns:send";

        /// <summary>Cancel a scheduled or running campaign.</summary>
        public const string Cancel = "campaigns:cancel";
    }

    /// <summary>Grants over reporting.</summary>
    public static class Reports
    {
        /// <summary>View dashboards and reports.</summary>
        public const string Read = "reports:read";

        /// <summary>Export report data.</summary>
        public const string Export = "reports:export";
    }

    /// <summary>Grants over the scheduler.</summary>
    public static class Scheduler
    {
        /// <summary>View job definitions, schedules and run history.</summary>
        public const string Read = "scheduler:read";

        /// <summary>Trigger, pause and resume jobs.</summary>
        public const string Manage = "scheduler:manage";
    }

    /// <summary>
    /// Default grants for each role, used by the seeder and by the role management screen.
    /// </summary>
    public static IReadOnlyList<string> ForRole(string role) => role switch
    {
        Roles.SuperAdmin => [Platform.All],

        Roles.Admin =>
        [
            Tenant.All,
            Users.Read, Users.Write, Users.ManageRoles,
            Contacts.Read, Contacts.Write, Contacts.Delete, Contacts.Import, Contacts.Export,
            Groups.Read, Groups.Write,
            Tags.Read, Tags.Write,
            WhatsApp.Read, WhatsApp.Connect,
            Templates.Read, Templates.Write, Templates.Sync,
            Campaigns.Read, Campaigns.Write, Campaigns.Send, Campaigns.Cancel,
            Reports.Read, Reports.Export,
            Scheduler.Read, Scheduler.Manage,
        ],

        // Deliberately excludes user management, billing, the WhatsApp connection, contact
        // deletion and export, and campaign dispatch. An employee builds the work; an admin
        // approves anything irreversible or involving bulk personal data.
        Roles.Employee =>
        [
            Contacts.Read, Contacts.Write, Contacts.Import,
            Groups.Read, Groups.Write,
            Tags.Read, Tags.Write,
            Templates.Read,
            Campaigns.Read, Campaigns.Write,
            Reports.Read,
        ],

        _ => [],
    };

    /// <summary>
    /// Returns whether a set of granted permissions satisfies a required one, honouring wildcards.
    /// </summary>
    /// <param name="granted">Permissions carried by the principal.</param>
    /// <param name="required">Permission being demanded.</param>
    public static bool IsSatisfiedBy(IEnumerable<string> granted, string required)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        foreach (var grant in granted)
        {
            if (Matches(grant, required))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a single grant covers a required permission.</summary>
    /// <param name="grant">A granted permission, possibly a wildcard.</param>
    /// <param name="required">Permission being demanded.</param>
    public static bool Matches(string grant, string required)
    {
        if (string.Equals(grant, required, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(grant, Platform.All, StringComparison.Ordinal))
        {
            return true;
        }

        if (!grant.EndsWith(WildcardSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        // "contacts:*" covers "contacts:read"; the trailing '*' is dropped and the remaining
        // "contacts:" prefix is matched, so "contacts:*" cannot accidentally match "contactsx:read".
        var prefix = grant[..^1];

        return required.StartsWith(prefix, StringComparison.Ordinal);
    }
}
