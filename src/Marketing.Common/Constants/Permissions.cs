namespace Marketing.Common.Constants;

/// <summary>
/// The permission catalogue.
/// <para>
/// <b>This list is a contract with the Angular client.</b> Every string here appears in the
/// <c>permissions</c> JWT claim and maps to a UI affordance and a route guard in
/// <c>permission.model.ts</c>. The client compares exact literals, so a typo or a rename silently
/// hides a feature rather than failing loudly. Add values; never rename one.
/// </para>
/// <para>
/// Format is <c>area.action</c>, dot separated. The API enforces these independently of the UI -
/// the client hiding a control is convenience, not security.
/// </para>
/// </summary>
public static class Permissions
{
    /// <summary>Dashboard and headline analytics.</summary>
    public static class Dashboard
    {
        /// <summary>View the dashboard.</summary>
        public const string View = "dashboard.view";

        /// <summary>View detailed statistics.</summary>
        public const string Statistics = "dashboard.statistics";

        /// <summary>Export dashboard data.</summary>
        public const string Export = "dashboard.export";
    }

    /// <summary>Contact book, groups and tags.</summary>
    public static class Contacts
    {
        /// <summary>List, search and view contacts.</summary>
        public const string View = "contacts.view";

        /// <summary>Create contacts.</summary>
        public const string Create = "contacts.create";

        /// <summary>Edit contacts.</summary>
        public const string Edit = "contacts.edit";

        /// <summary>Delete contacts.</summary>
        public const string Delete = "contacts.delete";

        /// <summary>Run the CSV import wizard.</summary>
        public const string Import = "contacts.import";

        /// <summary>
        /// Discover businesses from a places provider and import them as contacts.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Import"/> because the two carry different risks. Uploading a
        /// file the user already holds costs nothing; discovering businesses spends metered provider
        /// credits against the workspace. Somebody trusted with a spreadsheet is not automatically
        /// trusted with the search budget.
        /// </remarks>
        public const string BusinessImport = "contacts.business_import";

        /// <summary>
        /// Export contacts. Separate from <see cref="View"/> because bulk extraction of personal
        /// data is the action a data-protection reviewer asks about.
        /// </summary>
        public const string Export = "contacts.export";

        /// <summary>Create, edit and delete contact groups.</summary>
        public const string GroupsManage = "groups.manage";

        /// <summary>Create, edit and delete tags.</summary>
        public const string TagsManage = "tags.manage";
    }

    /// <summary>WhatsApp connection, templates and campaigns.</summary>
    public static class WhatsApp
    {
        /// <summary>Run Embedded Signup and connect a number.</summary>
        public const string Connect = "whatsapp.connect";

        /// <summary>Disconnect the WhatsApp Business Account.</summary>
        public const string Disconnect = "whatsapp.disconnect";

        /// <summary>Read the shared inbox and its conversation history.</summary>
        public const string InboxView = "whatsapp.inbox.view";

        /// <summary>
        /// Reply to a customer in the inbox.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="InboxView"/> on purpose. Reading a conversation to answer a
        /// question about an order is routine; sending a message that reaches a customer's handset
        /// under the workspace's own verified name is not, and the two are commonly granted to
        /// different people.
        /// </remarks>
        public const string InboxReply = "whatsapp.inbox.reply";

        /// <summary>View message templates.</summary>
        public const string TemplatesView = "whatsapp.templates.view";

        /// <summary>Trigger a template synchronisation with Meta.</summary>
        public const string TemplatesSync = "whatsapp.templates.sync";

        /// <summary>Create campaigns.</summary>
        public const string CampaignsCreate = "whatsapp.campaigns.create";

        /// <summary>Edit campaigns.</summary>
        public const string CampaignsEdit = "whatsapp.campaigns.edit";

        /// <summary>Delete campaigns.</summary>
        public const string CampaignsDelete = "whatsapp.campaigns.delete";

        /// <summary>Schedule a campaign for later dispatch.</summary>
        public const string CampaignsSchedule = "whatsapp.campaigns.schedule";

        /// <summary>
        /// Dispatch a campaign. Deliberately separate from <see cref="CampaignsCreate"/> so
        /// drafting and sending can be split between an operator and an approver.
        /// </summary>
        public const string CampaignsSend = "whatsapp.campaigns.send";

        /// <summary>Pause a running campaign.</summary>
        public const string CampaignsPause = "whatsapp.campaigns.pause";

        /// <summary>Cancel a scheduled or running campaign.</summary>
        public const string CampaignsCancel = "whatsapp.campaigns.cancel";

        /// <summary>View campaign delivery reports.</summary>
        public const string CampaignsReports = "whatsapp.campaigns.reports";
    }

    /// <summary>Email channel.</summary>
    public static class Email
    {
        /// <summary>Connect an email sending account.</summary>
        public const string Connect = "email.connect";

        /// <summary>Create and edit email templates.</summary>
        public const string TemplatesManage = "email.templates.manage";

        /// <summary>Create email campaigns.</summary>
        public const string CampaignsCreate = "email.campaigns.create";

        /// <summary>Send email campaigns.</summary>
        public const string CampaignsSend = "email.campaigns.send";

        /// <summary>View email analytics.</summary>
        public const string AnalyticsView = "email.analytics.view";
    }

    /// <summary>Social channels.</summary>
    public static class Social
    {
        /// <summary>Connect a social account.</summary>
        public const string AccountsConnect = "social.accounts.connect";

        /// <summary>Create posts.</summary>
        public const string PostsCreate = "social.posts.create";

        /// <summary>Schedule posts.</summary>
        public const string PostsSchedule = "social.posts.schedule";

        /// <summary>Publish posts.</summary>
        public const string PostsPublish = "social.posts.publish";

        /// <summary>Delete posts.</summary>
        public const string PostsDelete = "social.posts.delete";

        /// <summary>View social analytics.</summary>
        public const string AnalyticsView = "social.analytics.view";
    }

    /// <summary>Reporting.</summary>
    public static class Reports
    {
        /// <summary>View reports.</summary>
        public const string View = "reports.view";

        /// <summary>Export report data.</summary>
        public const string Export = "reports.export";

        /// <summary>Download a report as CSV.</summary>
        public const string DownloadCsv = "reports.download.csv";

        /// <summary>Download a report as Excel.</summary>
        public const string DownloadExcel = "reports.download.excel";

        /// <summary>Download a report as PDF.</summary>
        public const string DownloadPdf = "reports.download.pdf";
    }

    /// <summary>Organisation settings.</summary>
    public static class Settings
    {
        /// <summary>Company profile and branding.</summary>
        public const string Company = "settings.company";

        /// <summary>Invite and manage employees and permission sets.</summary>
        public const string Employees = "settings.employees";

        /// <summary>View invoices, payments and renewals.</summary>
        public const string Billing = "settings.billing";

        /// <summary>View and change the subscription.</summary>
        public const string Subscription = "settings.subscription";

        /// <summary>Manage third-party integrations.</summary>
        public const string Integrations = "settings.integrations";

        /// <summary>Issue and revoke API keys.</summary>
        public const string ApiKeys = "settings.apikeys";
    }

    /// <summary>Platform administration. Granted to <see cref="Roles.SuperAdmin"/> only.</summary>
    public static class Platform
    {
        /// <summary>Manage tenant organisations.</summary>
        public const string Tenants = "platform.tenants";

        /// <summary>Read the platform-wide audit log.</summary>
        public const string Audit = "platform.audit";

        /// <summary>View infrastructure health and monitoring.</summary>
        public const string Monitoring = "platform.monitoring";

        /// <summary>Create and edit subscription plans.</summary>
        public const string Plans = "platform.plans";
    }

    /// <summary>
    /// The floor every member of a workspace holds, whatever anybody grants or revokes.
    /// </summary>
    /// <remarks>
    /// Deliberately one permission, not a starter pack. Without <see cref="Dashboard.View"/> there
    /// is no landing route, so a new employee signs in and every screen - including the one they
    /// arrive on - reports a permission error. That is not "an empty application waiting for
    /// access"; it is an account that appears broken to the person using it.
    /// <para>
    /// It is a floor rather than a default: an administrator can add to it but cannot revoke it,
    /// because doing so produces an account that can authenticate and then do nothing at all. Keep
    /// this list short - everything added here is granted to people nobody chose to grant it to,
    /// which is the opposite failure and just as real.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> Baseline = [Dashboard.View];

    /// <summary>Every permission the platform recognises.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Dashboard.View, Dashboard.Statistics, Dashboard.Export,

        Contacts.View, Contacts.Create, Contacts.Edit, Contacts.Delete,
        Contacts.Import, Contacts.BusinessImport, Contacts.Export, Contacts.GroupsManage,
        Contacts.TagsManage,

        WhatsApp.Connect, WhatsApp.Disconnect, WhatsApp.InboxView, WhatsApp.InboxReply,
        WhatsApp.TemplatesView, WhatsApp.TemplatesSync,
        WhatsApp.CampaignsCreate, WhatsApp.CampaignsEdit, WhatsApp.CampaignsDelete,
        WhatsApp.CampaignsSchedule, WhatsApp.CampaignsSend, WhatsApp.CampaignsPause,
        WhatsApp.CampaignsCancel, WhatsApp.CampaignsReports,

        Email.Connect, Email.TemplatesManage, Email.CampaignsCreate, Email.CampaignsSend,
        Email.AnalyticsView,

        Social.AccountsConnect, Social.PostsCreate, Social.PostsSchedule, Social.PostsPublish,
        Social.PostsDelete, Social.AnalyticsView,

        Reports.View, Reports.Export, Reports.DownloadCsv, Reports.DownloadExcel, Reports.DownloadPdf,

        Settings.Company, Settings.Employees, Settings.Billing, Settings.Subscription,
        Settings.Integrations, Settings.ApiKeys,

        Platform.Tenants, Platform.Audit, Platform.Monitoring, Platform.Plans,
    ];

    /// <summary>Permissions reserved for platform staff.</summary>
    public static readonly IReadOnlyList<string> PlatformOnly =
        [Platform.Tenants, Platform.Audit, Platform.Monitoring, Platform.Plans];

    /// <summary>
    /// Default grant for each role, used by the seeder and the role management screen.
    /// <para>
    /// An Admin gets everything except the platform group. An Employee starts small - the contract
    /// describes it as "a small permission grant that an Admin extends" - and deliberately excludes
    /// deletion, export, dispatch, billing and user management.
    /// </para>
    /// </summary>
    /// <param name="role">Role name from <see cref="Roles"/>.</param>
    public static IReadOnlyList<string> ForRole(string role) => role switch
    {
        Roles.SuperAdmin => All,

        Roles.Admin => [.. All.Except(PlatformOnly, StringComparer.Ordinal)],

        Roles.Employee =>
        [
            Dashboard.View,
            Contacts.View, Contacts.Create, Contacts.Edit, Contacts.Import,
            Contacts.GroupsManage, Contacts.TagsManage,
            WhatsApp.TemplatesView,
            WhatsApp.CampaignsCreate, WhatsApp.CampaignsEdit, WhatsApp.CampaignsReports,
            Reports.View,
        ],

        _ => [],
    };

    /// <summary>Returns whether a string is a permission the platform recognises.</summary>
    /// <param name="permission">Candidate permission.</param>
    public static bool IsKnown(string? permission) =>
        permission is not null && All.Contains(permission, StringComparer.Ordinal);

    /// <summary>
    /// Returns whether a granted set satisfies a required permission.
    /// <para>
    /// Exact matching only. The catalogue is explicit and closed, so wildcard grants would create
    /// a second, looser way to express authority and make "who can do X" harder to answer.
    /// </para>
    /// </summary>
    /// <param name="granted">Permissions carried by the principal.</param>
    /// <param name="required">Permission being demanded.</param>
    public static bool IsSatisfiedBy(IEnumerable<string> granted, string required)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        return granted.Contains(required, StringComparer.Ordinal);
    }
}
