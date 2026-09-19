using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One customer's thread with this workspace, and the clock that governs it.
/// </summary>
/// <remarks>
/// Keyed on the customer's WhatsApp number rather than on a contact, because a thread starts the
/// moment somebody messages the business - long before anyone decides to save them as a contact.
/// <para>
/// <see cref="WindowExpiresAt"/> is the field the whole inbox turns on. Meta allows free-form
/// replies only for 24 hours after the customer's last inbound message; outside that window an
/// approved template is the only way through. Stored rather than derived so a query can order and
/// filter by it, and refreshed by every inbound message.
/// </para>
/// </remarks>
public sealed class Conversation : BaseEntity, IRequiresTenant
{
    /// <summary>Customer's WhatsApp number, normalised to digits.</summary>
    public required string WaId { get; set; }

    /// <summary>Saved contact, when the number matches one. Null is normal, not an error.</summary>
    public long? ContactId { get; set; }

    /// <summary>Name to show in the list: the contact's, or the profile name Meta sent.</summary>
    public string ContactName { get; set; } = string.Empty;

    /// <summary>First line of the most recent message, for the conversation list.</summary>
    public string LastMessagePreview { get; set; } = string.Empty;

    /// <summary>When the most recent message arrived or was sent.</summary>
    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>Inbound messages nobody has opened yet.</summary>
    public int UnreadCount { get; set; }

    /// <summary>
    /// The number the customer wrote to. Replies always go out from it, never from the default.
    /// </summary>
    /// <remarks>
    /// Nullable only for threads that predate numbers being separate; the migration fills them in
    /// with the workspace's first number.
    /// </remarks>
    public long? WhatsAppConnectionId { get; set; }

    /// <summary>The agent who has taken this conversation, if anyone has.</summary>
    public long? AssignedToUserId { get; set; }

    /// <summary>
    /// When free-form replies stop being allowed: the last inbound message plus 24 hours. Null when
    /// no window has ever opened.
    /// </summary>
    public DateTimeOffset? WindowExpiresAt { get; set; }

    /// <summary>Messages in this thread.</summary>
    public List<ConversationMessage> Messages { get; set; } = [];
}

/// <summary>One message in a conversation, in either direction.</summary>
/// <remarks>
/// <see cref="MetaMessageId"/> is unique because Meta redelivers webhooks: the same inbound message
/// can arrive several times, and a duplicate must be a no-op rather than a second bubble in the
/// thread. It is also how a delivery receipt finds the message it concerns.
/// </remarks>
public sealed class ConversationMessage : BaseEntity, IRequiresTenant
{
    /// <summary>Thread this message belongs to.</summary>
    public long ConversationId { get; set; }

    /// <summary>Meta's message identifier. Null only for a message not yet accepted by Meta.</summary>
    public string? MetaMessageId { get; set; }

    /// <summary>Who sent it.</summary>
    public MessageDirection Direction { get; set; }

    /// <summary>What kind of message it is.</summary>
    public ConversationMessageKind Kind { get; set; } = ConversationMessageKind.Text;

    /// <summary>Text body, or a readable description for a kind that carries none.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Attachment, when there is one.</summary>
    public long? MediaId { get; set; }

    /// <summary>Delivery state, which only ever moves forwards.</summary>
    public InboxMessageStatus Status { get; set; } = InboxMessageStatus.Queued;

    /// <summary>Why a failed message failed, in Meta's words.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Template used, for an outbound template send, so the thread can label it.</summary>
    public string? TemplateName { get; set; }


    /// <summary>
    /// Whether the assistant wrote this rather than a person.
    /// </summary>
    /// <remarks>
    /// The monthly allowance is counted from these rows, so the counter cannot drift from what was
    /// actually sent. It also lets the thread mark an answer as automatic, which a customer service
    /// team needs when they read back what a customer was told.
    /// </remarks>
    public bool IsAutoReply { get; set; }

    /// <summary>When it happened, as reported by Meta for inbound messages.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Thread navigation.</summary>
    public Conversation Conversation { get; set; } = null!;

    /// <summary>Attachment navigation.</summary>
    public MediaAsset? Media { get; set; }
}

/// <summary>A file exchanged with a customer, held by this platform rather than by Meta.</summary>
/// <remarks>
/// Meta's media ids expire after 30 days, so a thread that stored only the id would render broken
/// images a few weeks later. The bytes are copied into this platform's own storage on the way in and
/// on the way out, which is also what makes them servable behind this platform's own authentication
/// and tenant filter.
/// </remarks>
public sealed class MediaAsset : BaseEntity, IRequiresTenant
{
    /// <summary>Meta's media identifier, kept for reference and for outbound sends.</summary>
    public string? MetaMediaId { get; set; }

    /// <summary>What the file is, which decides the limits it was checked against.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Original file name, as uploaded or as Meta reported it.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Media type, used when serving the bytes back.</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Key in the file store holding the bytes.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>When it was stored.</summary>
    public DateTimeOffset UploadedAt { get; set; }
}
