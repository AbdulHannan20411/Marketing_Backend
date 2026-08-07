using static Marketing.Common.Constants.AppConstants;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One message in one campaign, to one contact.
/// <para>
/// This row is what makes dispatch survivable. A campaign of fifty thousand recipients will be
/// interrupted - a deploy, a restart, a Meta outage - and without a per-recipient record the only
/// options after an interruption are to re-send to everybody or to send to nobody. With it, the
/// dispatcher resumes exactly where it stopped.
/// </para>
/// <para>
/// It is also the correlation key for delivery receipts. Meta's webhook reports status against its
/// own message id and nothing else, so without this row an incoming "delivered" cannot be matched
/// to a contact or a campaign.
/// </para>
/// </summary>
public sealed class CampaignMessage : BaseEntity, IRequiresTenant
{
    /// <summary>Campaign this message belongs to.</summary>
    public Guid CampaignId { get; set; }

    /// <summary>Recipient.</summary>
    public Guid ContactId { get; set; }

    /// <summary>Recipient number at the time of sending, in case the contact later changes it.</summary>
    public required string PhoneNumber { get; set; }

    /// <summary>Where this message has got to.</summary>
    public CampaignMessageStatus Status { get; set; } = CampaignMessageStatus.Pending;

    /// <summary>
    /// Meta's identifier for the accepted message. Delivery and read receipts arrive keyed by this.
    /// </summary>
    public string? MetaMessageId { get; set; }

    /// <summary>How many send attempts have been made. Bounded, so a poison row cannot loop.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Instant Meta accepted the message.</summary>
    public DateTimeOffset? SentOn { get; set; }

    /// <summary>Instant Meta reported delivery.</summary>
    public DateTimeOffset? DeliveredOn { get; set; }

    /// <summary>Instant the recipient opened it.</summary>
    public DateTimeOffset? ReadOn { get; set; }

    /// <summary>Meta's numeric error code, when the send or delivery failed.</summary>
    public int? ErrorCode { get; set; }

    /// <summary>Human-readable failure reason.</summary>
    public string? ErrorReason { get; set; }

    /// <summary>Campaign navigation.</summary>
    public Campaign Campaign { get; set; } = null!;

    /// <summary>Contact navigation.</summary>
    public Contact Contact { get; set; } = null!;
}
