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
    public Guid? MessageTemplateId { get; set; }

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

    /// <summary>Instant dispatch is scheduled for.</summary>
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>Instant dispatch finished.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Display name of the person who created it.</summary>
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>Template navigation.</summary>
    public MessageTemplate? MessageTemplate { get; set; }
}

/// <summary>One message that could not be delivered.</summary>
public sealed class DeliveryFailure : BaseEntity, IRequiresTenant
{
    /// <summary>Campaign the message belonged to.</summary>
    public Guid? CampaignId { get; set; }

    /// <summary>Campaign name, denormalised so the failures report needs no join.</summary>
    public string CampaignName { get; set; } = string.Empty;

    /// <summary>Contact the message was addressed to.</summary>
    public Guid? ContactId { get; set; }

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
