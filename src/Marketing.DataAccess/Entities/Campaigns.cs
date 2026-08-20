using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>A bulk message send to a defined audience.</summary>
public sealed class Campaign : BaseEntity, IRequiresTenant
{
    /// <summary>Campaign name.</summary>
    public required string Name { get; set; }

    /// <summary>Template used, denormalised for display so the list needs no join.</summary>
    public required string TemplateName { get; set; }

    /// <summary>Template used.</summary>
    public long? MessageTemplateId { get; set; }

    /// <summary>Lifecycle state.</summary>
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;

    /// <summary>Human-readable audience description, for example "Loyalty members".</summary>
    public string AudienceLabel { get; set; } = string.Empty;

    /// <summary>Contacts in the audience.</summary>
    public int AudienceSize { get; set; }

    /// <summary>Messages accepted by Meta.</summary>
    public int SentCount { get; set; }

    /// <summary>Messages Meta confirmed delivered.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Messages the recipient opened.</summary>
    public int ReadCount { get; set; }

    /// <summary>Messages whose button or link was clicked.</summary>
    public int ClickedCount { get; set; }

    /// <summary>Messages that failed.</summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Groups making up the audience.
    /// <para>
    /// Persisted rather than recomputed from a label, because the dispatcher runs minutes or days
    /// after the campaign was composed and has to be able to reconstruct exactly who was chosen.
    /// </para>
    /// </summary>
    public List<long> AudienceGroupIds { get; set; } = [];

    /// <summary>Free-text description shown on the detail page.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The recurrence rule as JSON, or null for a one-off.
    /// <para>
    /// Stored serialised rather than shredded into columns. The rule is read whole, written whole
    /// and never queried by any of its parts - the dispatcher selects on <see cref="NextRunAtUtc"/>
    /// - so columns would buy nothing and cost a migration every time the client adds a field.
    /// </para>
    /// </summary>
    public string? RecurrenceJson { get; set; }

    /// <summary>
    /// IANA zone the rule is expressed in, denormalised out of <see cref="RecurrenceJson"/>.
    /// <para>
    /// Duplicated deliberately: an operator reading the table, and any report grouping campaigns by
    /// region, should not have to parse JSON to answer "what time does this actually run".
    /// </para>
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Instant the next occurrence is due, in UTC. Indexed; this is the dispatcher's entry point.
    /// <para>
    /// Recomputed after each firing rather than pre-enumerated. A campaign that never ends has no
    /// list of occurrences to store, and a stored list is stale the moment the rule is edited.
    /// </para>
    /// </summary>
    public DateTimeOffset? NextRunAtUtc { get; set; }

    /// <summary>Instant the most recent occurrence fired.</summary>
    public DateTimeOffset? LastRunAtUtc { get; set; }

    /// <summary>
    /// Occurrences claimed so far, including skipped ones.
    /// <para>
    /// The counter an <c>afterCount</c> end condition is measured against, and the source of each
    /// run's occurrence number. Counted rather than derived from the run table so that the value
    /// the uniqueness constraint depends on is settled before any row is written.
    /// </para>
    /// </summary>
    public int OccurrencesRun { get; set; }

    /// <summary>Instant dispatch is scheduled for.</summary>
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>
    /// Instant the recipient rows were written.
    /// <para>
    /// The dispatcher materialises the audience once and works from that snapshot. Set means "do
    /// not enumerate the groups again" - a contact added to a group mid-dispatch is not silently
    /// pulled into a campaign that was already counted and billed.
    /// </para>
    /// </summary>
    public DateTimeOffset? RecipientsQueuedOn { get; set; }

    /// <summary>
    /// Instant a rate-limited campaign becomes eligible again.
    /// <para>
    /// Set when the tenant's rolling 24-hour messaging allowance is exhausted mid-dispatch. The
    /// poller resumes the campaign on its own once this passes, so nobody has to notice and press
    /// send again.
    /// </para>
    /// </summary>
    public DateTimeOffset? ResumeAfter { get; set; }

    /// <summary>Instant dispatch finished.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Display name of the person who created it.</summary>
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>Template navigation.</summary>
    public MessageTemplate? MessageTemplate { get; set; }

    /// <summary>Firings of this campaign, newest last.</summary>
    public List<CampaignRun> Runs { get; set; } = [];
}

/// <summary>
/// One firing of a campaign.
/// <para>
/// A recurring campaign is not one send but many, and a single set of counters on the definition
/// cannot say whether "4,210 sent" means last Monday or every Monday since September. The campaign
/// holds lifetime totals; each run holds its own.
/// </para>
/// <para>
/// The row is also the idempotency claim. It is inserted inside the dispatch transaction before any
/// message goes out, and <c>(CampaignId, OccurrenceNumber)</c> is unique, so a redelivery, a second
/// poller in the same minute, or a retry after a timeout all collide here rather than sending twice.
/// </para>
/// </summary>
public sealed class CampaignRun : BaseEntity, IRequiresTenant
{
    /// <summary>Campaign this run belongs to.</summary>
    public long CampaignId { get; set; }

    /// <summary>
    /// One-based position in the series. Counts skipped occurrences too, so the numbering matches
    /// the schedule rather than only the sends that happened.
    /// </summary>
    public int OccurrenceNumber { get; set; }

    /// <summary>Lifecycle state.</summary>
    public CampaignRunStatus Status { get; set; } = CampaignRunStatus.Pending;

    /// <summary>Instant this occurrence was due, in UTC.</summary>
    public DateTimeOffset ScheduledForUtc { get; set; }

    /// <summary>Instant dispatch began.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Instant dispatch finished.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Why the run failed or was skipped. Null when it succeeded.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Distinct contacts resolved at fire time.</summary>
    public int AudienceSize { get; set; }

    /// <summary>Messages accepted by Meta.</summary>
    public int SentCount { get; set; }

    /// <summary>Messages Meta confirmed delivered.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Messages the recipient opened.</summary>
    public int ReadCount { get; set; }

    /// <summary>Messages whose button or link was clicked.</summary>
    public int ClickedCount { get; set; }

    /// <summary>Messages that failed.</summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Recipients deliberately not messaged - over a plan limit, opted out, or beyond the tier.
    /// </summary>
    public int SkippedCount { get; set; }

    /// <summary>Campaign navigation.</summary>
    public Campaign? Campaign { get; set; }

    /// <summary>Recipients of this run.</summary>
    public List<CampaignRecipient> Recipients { get; set; } = [];
}

/// <summary>
/// One contact addressed by one run.
/// <para>
/// This is what lets a crashed dispatch resume without messaging anyone twice: the row is written
/// before the send and <c>(RunId, ContactId)</c> is unique, so a resumed worker skips whatever it
/// already claimed. It is also what delivery receipts are matched against, since Meta returns its
/// own message id and nothing else that identifies the send.
/// </para>
/// </summary>
public sealed class CampaignRecipient : BaseEntity, IRequiresTenant
{
    /// <summary>Run this recipient belongs to.</summary>
    public long CampaignRunId { get; set; }

    /// <summary>Contact addressed.</summary>
    public long ContactId { get; set; }

    /// <summary>Number the message was addressed to, as it stood at fire time.</summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Delivery state.</summary>
    public CampaignMessageStatus Status { get; set; } = CampaignMessageStatus.Pending;

    /// <summary>Meta's message identifier, once accepted.</summary>
    public string? MetaMessageId { get; set; }

    /// <summary>Why this recipient failed or was skipped.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Run navigation.</summary>
    public CampaignRun? CampaignRun { get; set; }
}

/// <summary>One message that could not be delivered.</summary>
public sealed class DeliveryFailure : BaseEntity, IRequiresTenant
{
    /// <summary>Campaign the message belonged to.</summary>
    public long? CampaignId { get; set; }

    /// <summary>Campaign name, denormalised so the failures report needs no join.</summary>
    public string CampaignName { get; set; } = string.Empty;

    /// <summary>Contact the message was addressed to.</summary>
    public long? ContactId { get; set; }

    /// <summary>Contact name, denormalised.</summary>
    public string ContactName { get; set; } = string.Empty;

    /// <summary>Recipient number.</summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Human-readable reason, for example "Invalid phone number".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Meta's numeric error code, for example 131026.</summary>
    public int ErrorCode { get; set; }

    /// <summary>Instant the failure was reported.</summary>
    public DateTimeOffset OccurredOn { get; set; }

    /// <summary>Campaign navigation.</summary>
    public Campaign? Campaign { get; set; }
}

/// <summary>
/// One day of messaging counters for a tenant.
/// <para>
/// Pre-aggregated deliberately. The dashboard and reports pages are on the critical path with a
/// sub-300 ms budget, and scanning a message log for thirty days on every request cannot meet it.
/// A nightly or streaming job maintains these rows; the read is thirty indexed lookups.
/// </para>
/// </summary>
public sealed class MessageDailyStat : BaseEntity, IRequiresTenant
{
    /// <summary>The day these counters cover, in UTC. Date only.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Messages accepted by Meta.</summary>
    public int Sent { get; set; }

    /// <summary>Messages confirmed delivered.</summary>
    public int Delivered { get; set; }

    /// <summary>Messages opened.</summary>
    public int Read { get; set; }

    /// <summary>Messages clicked.</summary>
    public int Clicked { get; set; }

    /// <summary>Messages that failed.</summary>
    public int Failed { get; set; }
}

/// <summary>A recorded action, surfaced on the dashboard activity feed.</summary>
public sealed class ActivityEntry : BaseEntity, IRequiresTenant
{
    /// <summary>Display name of the person who acted.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Verb phrase, for example "launched campaign".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What was acted on, for example "Loyalty Points Reminder".</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Instant the action happened.</summary>
    public DateTimeOffset OccurredOn { get; set; }
}
