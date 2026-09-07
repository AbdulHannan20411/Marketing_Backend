using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// A transactional email waiting to be delivered.
/// </summary>
/// <remarks>
/// Written inside the same transaction as whatever caused it, then delivered by a poller. Sending
/// inline held a database transaction open for the whole SMTP conversation - measured at over a
/// second to Gmail before authentication even began - so creating an administrator cost seconds of
/// pure network wait while holding locks, and a slow relay could stall the request for the full
/// thirty-second timeout.
/// <para>
/// Deliberately not tenant-scoped. Platform invitations are sent by a Super Admin before the
/// recipient's tenant exists, and the poller runs with no signed-in user, so a tenant filter here
/// would hide exactly the rows that most need sending.
/// </para>
/// </remarks>
public sealed class OutboxEmail : BaseEntity
{
    /// <summary>Recipient address.</summary>
    public required string ToAddress { get; set; }

    /// <summary>Recipient display name.</summary>
    public string ToName { get; set; } = string.Empty;

    /// <summary>Subject line.</summary>
    public required string Subject { get; set; }

    /// <summary>HTML body.</summary>
    public required string HtmlBody { get; set; }

    /// <summary>Plain-text alternative.</summary>
    public required string TextBody { get; set; }

    /// <summary>Address replies go to, when a person rather than the platform caused the message.</summary>
    public string? ReplyToAddress { get; set; }

    /// <summary>Display name for <see cref="ReplyToAddress"/>.</summary>
    public string? ReplyToName { get; set; }

    /// <summary>Delivery state.</summary>
    public OutboxEmailStatus Status { get; set; } = OutboxEmailStatus.Pending;

    /// <summary>How many delivery attempts have been made.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Earliest instant the next attempt may run.
    /// </summary>
    /// <remarks>
    /// Carries the backoff. A relay that is refusing mail now is usually still refusing it a second
    /// later, and retrying in a tight loop is how a transient outage becomes a rate-limit ban.
    /// </remarks>
    public DateTimeOffset NextAttemptOn { get; set; }

    /// <summary>Instant the message was accepted by the relay.</summary>
    public DateTimeOffset? SentOn { get; set; }

    /// <summary>Why the last attempt failed, for an operator reading a support ticket.</summary>
    public string? LastError { get; set; }
}
