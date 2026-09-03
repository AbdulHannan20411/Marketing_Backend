using System.Text.Json.Serialization;

namespace Marketing.Common.Constants;

/// <summary>
/// Enumerations that cross the wire to the Angular client.
/// <para>
/// <b>Every value here is compared as an exact string literal by the front end.</b> They serialise
/// as camelCase strings; the handful whose wire value is not the camelCase of the member name
/// carry an explicit <see cref="JsonStringEnumMemberNameAttribute"/>. Renaming a member changes the
/// wire value and breaks a badge or a filter silently, with no error anywhere.
/// </para>
/// <para>
/// Held apart from <see cref="AppConstants"/> only because of volume - these are the transport
/// contract, whereas the enums nested in AppConstants are internal platform state.
/// </para>
/// </summary>
public static class ContractEnums
{
    // -------------------------------------------------------------------------------------
    // Contacts
    // -------------------------------------------------------------------------------------

    /// <summary>Marketing consent state of a contact.</summary>
    public enum ContactStatus
    {
        /// <summary>Opted in and reachable.</summary>
        Subscribed,

        /// <summary>Opted out. Excluded from every campaign audience.</summary>
        Unsubscribed,

        /// <summary>Blocked by the tenant or by Meta.</summary>
        Blocked,
    }

    /// <summary>Badge colour for a tag.</summary>
    public enum TagColor
    {
        /// <summary>Primary brand green.</summary>
        Brand,

        /// <summary>Informational.</summary>
        Info,

        /// <summary>Warning.</summary>
        Warning,

        /// <summary>Danger.</summary>
        Danger,

        /// <summary>Neutral grey.</summary>
        Neutral,
    }

    /// <summary>
    /// Commercial stage of a contact.
    /// <para>
    /// Added to make the lead and customer counts on the platform overview real rather than
    /// invented. If leads and customers turn out to be first-class entities rather than a contact
    /// attribute, this becomes their own module and this enum goes away.
    /// </para>
    /// </summary>
    public enum ContactLifecycle
    {
        /// <summary>Has not transacted.</summary>
        Lead,

        /// <summary>Has transacted at least once.</summary>
        Customer,
    }

    /// <summary>State of a staged CSV import.</summary>
    public enum ContactImportStatus
    {
        /// <summary>Accepted and stored; the parse has not started.</summary>
        Queued,

        /// <summary>A worker is reading and validating the file.</summary>
        Processing,

        /// <summary>Parsed; waiting for the operator to map columns.</summary>
        AwaitingMapping,

        /// <summary>Mapped and previewed; waiting for the operator to confirm.</summary>
        AwaitingConfirmation,

        /// <summary>A worker is writing contacts.</summary>
        Committing,

        /// <summary>Finished with every usable row written.</summary>
        Completed,

        /// <summary>Finished, but some rows could not be used. Their reasons are on the batch.</summary>
        CompletedWithErrors,

        /// <summary>The file could not be read or the run was abandoned by the worker.</summary>
        Failed,

        /// <summary>Cancelled by the operator, or expired before it was confirmed.</summary>
        Cancelled,
    }

    // The three enums below serialise PascalCase, against the camelCase policy every other enum in
    // this file follows. The import contract was specified that way and the client matches on the
    // literals, so the exception is declared per type rather than by loosening the global policy.

    /// <summary>What happened to one staged import row.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RowStatus>))]
    public enum RowStatus
    {
        /// <summary>Staged, not yet examined.</summary>
        Pending,

        /// <summary>Parsed and valid; not yet committed.</summary>
        Valid,

        /// <summary>Written as a new contact.</summary>
        Imported,

        /// <summary>Applied over an existing contact.</summary>
        Updated,

        /// <summary>Its number already exists, in the file or in the tenant.</summary>
        Duplicate,

        /// <summary>Deliberately passed over.</summary>
        Skipped,

        /// <summary>Could not be used.</summary>
        Failed,
    }

    /// <summary>
    /// Why an import row could not be used.
    /// <para>
    /// The client owns the wording and matches on the code. A message always travels alongside, so
    /// a code added later renders its message rather than breaking the screen.
    /// </para>
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ImportErrorCode>))]
    public enum ImportErrorCode
    {
        /// <summary>The number could not be parsed or dialled.</summary>
        InvalidPhoneNumber,

        /// <summary>A required field was empty.</summary>
        MissingRequiredField,

        /// <summary>The email address is not valid.</summary>
        InvalidEmail,

        /// <summary>The number already belongs to a stored contact.</summary>
        DuplicateContact,

        /// <summary>The number appears earlier in the same file.</summary>
        DuplicateInFile,

        /// <summary>A mapped column is not one this import understands.</summary>
        UnsupportedColumn,

        /// <summary>The country is not one we recognise.</summary>
        InvalidCountry,

        /// <summary>The write failed for a reason the operator cannot fix.</summary>
        DatabaseError,

        /// <summary>The plan's contact ceiling was reached before this row.</summary>
        PlanLimitExceeded,
    }

    /// <summary>Where a failed-record export has got to.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ExportStatus>))]
    public enum ExportStatus
    {
        /// <summary>Queued.</summary>
        Pending,

        /// <summary>Being generated.</summary>
        Processing,

        /// <summary>Ready to download.</summary>
        Completed,

        /// <summary>Generation failed.</summary>
        Failed,
    }

    // -------------------------------------------------------------------------------------
    // WhatsApp
    // -------------------------------------------------------------------------------------

    /// <summary>State of the Meta WhatsApp Business Account connection.</summary>
    public enum ConnectionStatus
    {
        /// <summary>Connected and usable.</summary>
        Connected,

        /// <summary>No number connected. The client renders a connect prompt.</summary>
        Disconnected,

        /// <summary>Embedded Signup started but not finished.</summary>
        Pending,

        /// <summary>Connected but failing - expired token, revoked permission.</summary>
        Error,
    }

    /// <summary>
    /// The stages a WhatsApp account passes through while being connected.
    /// </summary>
    /// <remarks>
    /// Reported individually because they fail for unrelated reasons with unrelated remedies. A
    /// single "could not connect" leaves an administrator with nowhere to go, while "the number is
    /// registered to another account" tells them exactly what to do next.
    /// </remarks>
    public enum OnboardingStep
    {
        /// <summary>Obtaining and validating the access token.</summary>
        Token,

        /// <summary>Subscribing the app to the account's webhooks.</summary>
        Subscribe,

        /// <summary>Registering the phone number for sending.</summary>
        Register,

        /// <summary>Reading the number and business profile back from Meta.</summary>
        Profile,
    }

    /// <summary>Outcome of a single <see cref="OnboardingStep"/>.</summary>
    public enum OnboardingStepStatus
    {
        /// <summary>Not started.</summary>
        Pending,

        /// <summary>In progress.</summary>
        Running,

        /// <summary>Completed successfully.</summary>
        Succeeded,

        /// <summary>Failed. Carries a code the client turns into a remedy.</summary>
        Failed,

        /// <summary>
        /// Deliberately not performed, and not a failure.
        /// <para>
        /// Registration is the case this exists for: a number onboarded through Embedded Signup or
        /// any Meta test number is already registered and rejects a second attempt. Reporting that
        /// as a failure made every test number look broken.
        /// </para>
        /// </summary>
        Skipped,
    }

    /// <summary>
    /// Meta's daily ceiling on how many <em>unique customers</em> a number may start conversations
    /// with.
    /// </summary>
    /// <remarks>
    /// Distinct from the rolling 24-hour message count the connection screen already shows. It caps
    /// conversations opened, not messages sent, so a campaign can sit inside the send limit and
    /// still be refused for reaching too many new people.
    /// <para>
    /// Meta decides it and raises it on quality and volume. It is read and reported, never
    /// requested, which is why the client renders it as a fact rather than a setting.
    /// </para>
    /// </remarks>
    public enum MessagingTier
    {
        /// <summary>250 unique customers a day. Where an unverified business starts.</summary>
        [JsonStringEnumMemberName("tier_250")]
        Tier250,

        /// <summary>1,000 unique customers a day.</summary>
        [JsonStringEnumMemberName("tier_1k")]
        Tier1K,

        /// <summary>10,000 unique customers a day.</summary>
        [JsonStringEnumMemberName("tier_10k")]
        Tier10K,

        /// <summary>100,000 unique customers a day.</summary>
        [JsonStringEnumMemberName("tier_100k")]
        Tier100K,

        /// <summary>No ceiling.</summary>
        [JsonStringEnumMemberName("unlimited")]
        Unlimited,
    }

    /// <summary>Meta's quality rating for a number or template.</summary>
    public enum QualityRating
    {
        /// <summary>High quality.</summary>
        Green,

        /// <summary>Degraded; messaging limits may be reduced.</summary>
        Yellow,

        /// <summary>Poor; at risk of restriction.</summary>
        Red,
    }

    /// <summary>Meta review status of a message template.</summary>
    public enum TemplateStatus
    {
        /// <summary>Approved and usable.</summary>
        Approved,

        /// <summary>Awaiting Meta review.</summary>
        Pending,

        /// <summary>Rejected. See the rejection reason.</summary>
        Rejected,

        /// <summary>Paused by Meta for quality reasons.</summary>
        Paused,
    }

    /// <summary>Meta template category. Determines pricing and consent rules.</summary>
    public enum TemplateCategory
    {
        /// <summary>Promotional. Requires marketing opt-in.</summary>
        Marketing,

        /// <summary>Transactional follow-up to a user action.</summary>
        Utility,

        /// <summary>One-time passcodes.</summary>
        Authentication,
    }

    // -------------------------------------------------------------------------------------
    // Campaigns
    // -------------------------------------------------------------------------------------

    /// <summary>Lifecycle state of a campaign.</summary>
    public enum CampaignStatus
    {
        /// <summary>Being composed.</summary>
        Draft,

        /// <summary>Scheduled for a future dispatch.</summary>
        Scheduled,

        /// <summary>Dispatch in progress.</summary>
        Sending,

        /// <summary>Dispatch finished.</summary>
        Completed,

        /// <summary>Dispatch paused mid-flight.</summary>
        Paused,

        /// <summary>Dispatch failed.</summary>
        Failed,
    }

    // -------------------------------------------------------------------------------------
    // Subscription and billing
    // -------------------------------------------------------------------------------------

    /// <summary>Billing cadence.</summary>
    public enum BillingCycle
    {
        /// <summary>Charged monthly.</summary>
        Monthly,

        /// <summary>Charged yearly.</summary>
        Yearly,
    }

    /// <summary>State of a tenant's subscription.</summary>
    public enum SubscriptionStatus
    {
        /// <summary>Paid and current.</summary>
        Active,

        /// <summary>Inside the trial window.</summary>
        Trial,

        /// <summary>Past the end date without renewal.</summary>
        Expired,

        /// <summary>Suspended, typically for non-payment.</summary>
        Suspended,

        /// <summary>Cancelled by the customer.</summary>
        Cancelled,
    }

    /// <summary>Support tier included in a plan.</summary>
    public enum SupportLevel
    {
        /// <summary>Community forum only.</summary>
        Community,

        /// <summary>Email support.</summary>
        Email,

        /// <summary>Priority email and chat.</summary>
        Priority,

        /// <summary>Dedicated account manager.</summary>
        Dedicated,
    }

    /// <summary>Availability of a subscription plan.</summary>
    public enum PlanStatus
    {
        /// <summary>Offered to customers.</summary>
        Active,

        /// <summary>Hidden from the pricing page but still honoured for existing subscribers.</summary>
        Inactive,

        /// <summary>Retired. Never offered again; existing subscribers keep their terms.</summary>
        Archived,
    }

    /// <summary>Feature modules a plan can switch on.</summary>
    public enum FeatureModule
    {
        /// <summary>
        /// WhatsApp channel. Pinned, because the camelCase policy would emit "whatsApp" and the
        /// contract's value is all lowercase.
        /// </summary>
        [JsonStringEnumMemberName("whatsapp")]
        WhatsApp,

        /// <summary>Email channel.</summary>
        Email,

        /// <summary>Social channels.</summary>
        Social,

        /// <summary>Contact management.</summary>
        Crm,

        /// <summary>Reporting and analytics.</summary>
        Reporting,

        /// <summary>AI assistance.</summary>
        Ai,

        /// <summary>Public API access.</summary>
        Api,

        /// <summary>Multiple employee seats.</summary>
        Employees,
    }

    /// <summary>Metric a plan limit applies to.</summary>
    public enum UsageMetricKey
    {
        /// <summary>Employee seats.</summary>
        Employees,

        /// <summary>Stored contacts.</summary>
        Contacts,

        /// <summary>Campaigns created.</summary>
        Campaigns,

        /// <summary>Connected WhatsApp accounts.</summary>
        WhatsAppAccounts,

        /// <summary>Connected email accounts.</summary>
        EmailAccounts,

        /// <summary>Connected social accounts.</summary>
        SocialAccounts,

        /// <summary>API calls this month.</summary>
        ApiCalls,

        /// <summary>Stored media, in megabytes.</summary>
        Storage,

        /// <summary>Messages sent today.</summary>
        MessagesDaily,

        /// <summary>Messages sent this month.</summary>
        MessagesMonthly,
    }

    /// <summary>State of an invoice.</summary>
    public enum InvoiceStatus
    {
        /// <summary>Settled.</summary>
        Paid,

        /// <summary>Issued and not yet due.</summary>
        Due,

        /// <summary>Past its due date and unpaid.</summary>
        Overdue,

        /// <summary>Refunded in full.</summary>
        Refunded,

        /// <summary>Cancelled before payment.</summary>
        Void,
    }

    /// <summary>Outcome of a payment attempt.</summary>
    public enum PaymentStatus
    {
        /// <summary>Captured.</summary>
        Succeeded,

        /// <summary>Declined or errored.</summary>
        Failed,

        /// <summary>Authorised, awaiting capture or bank settlement.</summary>
        Pending,

        /// <summary>Returned to the payer.</summary>
        Refunded,
    }

    /// <summary>How a payment was made.</summary>
    public enum PaymentMethodKind
    {
        /// <summary>Card payment.</summary>
        Card,

        /// <summary>
        /// Bank transfer. The wire value is snake_case by contract, so it is pinned explicitly -
        /// the camelCase policy would otherwise emit "bankTransfer" and the client would not match.
        /// </summary>
        [JsonStringEnumMemberName("bank_transfer")]
        BankTransfer,

        /// <summary>PayPal.</summary>
        PayPal,
    }

    // -------------------------------------------------------------------------------------
    // Employees
    // -------------------------------------------------------------------------------------

    /// <summary>State of an employee account, as the client renders it.</summary>
    public enum EmployeeStatus
    {
        /// <summary>Active and able to sign in.</summary>
        Active,

        /// <summary>Invited but has not accepted.</summary>
        Invited,

        /// <summary>Disabled by an administrator.</summary>
        Suspended,
    }

    // -------------------------------------------------------------------------------------
    // Notifications
    // -------------------------------------------------------------------------------------

    /// <summary>Severity of a notification, driving its colour and ordering.</summary>
    public enum NotificationPriority
    {
        /// <summary>Needs attention now.</summary>
        Critical,

        /// <summary>Needs attention soon.</summary>
        Warning,

        /// <summary>Informational.</summary>
        Info,

        /// <summary>Confirmation of something that went well.</summary>
        Success,
    }

    /// <summary>
    /// What a notification is about.
    /// <para>
    /// Wire values are dotted, which no naming policy produces, so every member pins its own.
    /// </para>
    /// </summary>
    public enum NotificationKind
    {
        /// <summary>The subscription is close to expiring.</summary>
        [JsonStringEnumMemberName("subscription.expiring")]
        SubscriptionExpiring,

        /// <summary>The Meta connection dropped.</summary>
        [JsonStringEnumMemberName("meta.disconnected")]
        MetaDisconnected,

        /// <summary>The WhatsApp access token is close to expiring.</summary>
        [JsonStringEnumMemberName("whatsapp.token.expiring")]
        WhatsAppTokenExpiring,

        /// <summary>A campaign finished.</summary>
        [JsonStringEnumMemberName("campaign.completed")]
        CampaignCompleted,

        /// <summary>A campaign failed.</summary>
        [JsonStringEnumMemberName("campaign.failed")]
        CampaignFailed,

        /// <summary>A payment was received.</summary>
        [JsonStringEnumMemberName("payment.received")]
        PaymentReceived,

        /// <summary>A payment failed.</summary>
        [JsonStringEnumMemberName("payment.failed")]
        PaymentFailed,

        /// <summary>An employee was invited.</summary>
        [JsonStringEnumMemberName("employee.invited")]
        EmployeeInvited,

        /// <summary>The plan was upgraded.</summary>
        [JsonStringEnumMemberName("plan.upgraded")]
        PlanUpgraded,

        /// <summary>Storage is close to its limit.</summary>
        [JsonStringEnumMemberName("storage.limit")]
        StorageLimit,

        /// <summary>Contacts are close to the plan limit.</summary>
        [JsonStringEnumMemberName("contacts.limit")]
        ContactsLimit,

        /// <summary>Messages are close to the plan limit.</summary>
        [JsonStringEnumMemberName("messages.limit")]
        MessagesLimit,

        /// <summary>A customer submitted proof of payment for review.</summary>
        [JsonStringEnumMemberName("payment.submitted")]
        PaymentSubmitted,

        /// <summary>A submitted payment was approved and the plan granted.</summary>
        [JsonStringEnumMemberName("payment.approved")]
        PaymentApproved,

        /// <summary>A submitted payment was rejected.</summary>
        [JsonStringEnumMemberName("payment.rejected")]
        PaymentRejected,
    }

    // -------------------------------------------------------------------------------------
    // Manual payment
    // -------------------------------------------------------------------------------------

    // Both enums below serialise PascalCase, against the camelCase policy the rest of this file
    // follows, because the manual-payment contract was specified that way and the client matches on
    // the literals. Declared per type rather than by loosening the global policy.

    /// <summary>How a customer sent the money.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PaymentChannel>))]
    public enum PaymentChannel
    {
        /// <summary>JazzCash mobile wallet.</summary>
        JazzCash,

        /// <summary>EasyPaisa mobile wallet.</summary>
        EasyPaisa,

        /// <summary>A direct bank transfer.</summary>
        BankTransfer,
    }

    /// <summary>Where a submitted payment has got to.</summary>
    /// <remarks>
    /// Distinct from <see cref="PaymentStatus"/>, which describes a captured payment. This one
    /// describes a human review, and only <see cref="Approved"/> grants a plan.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<PaymentRequestStatus>))]
    public enum PaymentRequestStatus
    {
        /// <summary>Submitted and awaiting review.</summary>
        Pending,

        /// <summary>Reviewed and accepted. The plan has been granted.</summary>
        Approved,

        /// <summary>Reviewed and refused. The reason is on the request.</summary>
        Rejected,

        /// <summary>Withdrawn by the customer before anyone reviewed it.</summary>
        Cancelled,
    }

    // -------------------------------------------------------------------------------------
    // Search and platform administration
    // -------------------------------------------------------------------------------------

    /// <summary>Category of a global search result.</summary>
    public enum SearchResultKind
    {
        /// <summary>A contact.</summary>
        Contact,

        /// <summary>A campaign.</summary>
        Campaign,

        /// <summary>A message template.</summary>
        Template,

        /// <summary>An employee.</summary>
        Employee,

        /// <summary>A report.</summary>
        Report,

        /// <summary>The subscription.</summary>
        Subscription,

        /// <summary>A settings page.</summary>
        Setting,
    }

    /// <summary>Commercial plan band shown on platform screens.</summary>
    public enum TenantPlan
    {
        /// <summary>Entry tier.</summary>
        Starter,

        /// <summary>Mid tier.</summary>
        Growth,

        /// <summary>Upper tier.</summary>
        Scale,

        /// <summary>Negotiated tier.</summary>
        Enterprise,
    }

    /// <summary>
    /// Tenant state as the platform screens render it.
    /// <para>
    /// Deliberately distinct from the internal <c>AppConstants.TenantStatus</c>, which has a
    /// Pending and a Cancelled state the client does not model. Mapping between them is explicit
    /// so a new internal state cannot leak out as an unrecognised string.
    /// </para>
    /// </summary>
    public enum TenantAccountStatus
    {
        /// <summary>Paying and operational.</summary>
        Active,

        /// <summary>Inside a trial.</summary>
        Trialing,

        /// <summary>Suspended.</summary>
        Suspended,
    }

    /// <summary>Severity of an audit-log entry.</summary>
    public enum AuditSeverity
    {
        /// <summary>Routine.</summary>
        Info,

        /// <summary>Notable.</summary>
        Warning,

        /// <summary>Security-relevant.</summary>
        Critical,
    }

    /// <summary>Health of a monitored service.</summary>
    public enum ServiceStatus
    {
        /// <summary>Healthy.</summary>
        Operational,

        /// <summary>Working but impaired.</summary>
        Degraded,

        /// <summary>Down.</summary>
        Outage,
    }

    /// <summary>How often a campaign repeats.</summary>
    public enum RecurrenceFrequency
    {
        /// <summary>A single dispatch. Every other rule field is ignored.</summary>
        Once,

        /// <summary>Every <c>interval</c> days.</summary>
        Daily,

        /// <summary>Every <c>interval</c> weeks, on the selected weekdays.</summary>
        Weekly,

        /// <summary>Every <c>interval</c> months.</summary>
        Monthly,

        /// <summary>Every <c>interval</c> years, in the selected month.</summary>
        Yearly,
    }

    /// <summary>How a monthly or yearly rule picks its day.</summary>
    public enum MonthlyMode
    {
        /// <summary>A fixed date, for example the 15th. Clamped in short months.</summary>
        DayOfMonth,

        /// <summary>An ordinal weekday, for example the third Wednesday.</summary>
        DayOfWeek,
    }

    /// <summary>Which occurrence of a weekday within a month.</summary>
    public enum MonthlyOrdinal
    {
        /// <summary>The first matching weekday.</summary>
        First,

        /// <summary>The second matching weekday.</summary>
        Second,

        /// <summary>The third matching weekday.</summary>
        Third,

        /// <summary>The fourth matching weekday.</summary>
        Fourth,

        /// <summary>
        /// The final matching weekday - the fourth or the fifth depending on the month, never a
        /// month that is skipped for want of a fifth.
        /// </summary>
        Last,
    }

    /// <summary>What stops a recurring campaign.</summary>
    public enum RecurrenceEndCondition
    {
        /// <summary>Nothing. It repeats until paused or cancelled.</summary>
        Never,

        /// <summary>A calendar date, in the campaign's own timezone.</summary>
        OnDate,

        /// <summary>A number of firings.</summary>
        AfterCount,
    }

    /// <summary>Lifecycle of one firing of a campaign.</summary>
    public enum CampaignRunStatus
    {
        /// <summary>Claimed, not yet dispatching.</summary>
        Pending,

        /// <summary>Dispatching.</summary>
        Running,

        /// <summary>Every recipient reached a terminal state.</summary>
        Completed,

        /// <summary>Abandoned. <c>failureReason</c> says why.</summary>
        Failed,

        /// <summary>
        /// Deliberately not sent - a missed occurrence collapsed by the catch-up policy, or an
        /// empty audience. Recorded rather than dropped so the history has no unexplained gaps.
        /// </summary>
        Skipped,
    }

    /// <summary>How far a user has got through the product tour.</summary>
    /// <remarks>
    /// Wire values are snake_case rather than the camelCase the rest of this file defaults to, so
    /// each carries an explicit name. The client compares them as exact string literals.
    /// </remarks>
    public enum OnboardingStatus
    {
        /// <summary>Never started. Also the answer for a user who has no stored state.</summary>
        [JsonStringEnumMemberName("not_started")]
        NotStarted,

        /// <summary>Started and not yet finished. <c>stepIndex</c> says where.</summary>
        [JsonStringEnumMemberName("in_progress")]
        InProgress,

        /// <summary>Walked to the end.</summary>
        [JsonStringEnumMemberName("completed")]
        Completed,

        /// <summary>Dismissed deliberately. Distinct from completed, so the two can be told apart.</summary>
        [JsonStringEnumMemberName("skipped")]
        Skipped,
    }

    /// <summary>Why an owner switched their workspace off.</summary>
    /// <remarks>
    /// Wire values are snake_case, so each carries an explicit name. The list is fixed and shared
    /// with the client; adding one means adding it there too or the radio simply will not appear.
    /// </remarks>
    public enum DeactivationReason
    {
        /// <summary>Cost.</summary>
        [JsonStringEnumMemberName("too_expensive")]
        TooExpensive,

        /// <summary>The platform does not do something they need.</summary>
        [JsonStringEnumMemberName("missing_features")]
        MissingFeatures,

        /// <summary>Going to a competitor.</summary>
        [JsonStringEnumMemberName("switching_provider")]
        SwitchingProvider,

        /// <summary>The need has gone away.</summary>
        [JsonStringEnumMemberName("no_longer_needed")]
        NoLongerNeeded,

        /// <summary>
        /// Coming back later.
        /// </summary>
        /// <remarks>
        /// Offered deliberately. Without it, people who intend to return pick something that
        /// misrepresents why they left, which makes the churn data worse rather than better - and
        /// these are the accounts most worth contacting.
        /// </remarks>
        [JsonStringEnumMemberName("temporary_pause")]
        TemporaryPause,

        /// <summary>Something else. Requires free text, or it says nothing at all.</summary>
        [JsonStringEnumMemberName("other")]
        Other,
    }
}
