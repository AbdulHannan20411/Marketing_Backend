using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>One customer's thread, as the conversation list shows it.</summary>
/// <param name="Id">Public identifier.</param>
/// <param name="ContactId">Saved contact, or null when the number is not one.</param>
/// <param name="ContactName">Name to show: the contact's, or the profile name Meta sent.</param>
/// <param name="PhoneNumber">Customer's number in display form.</param>
/// <param name="LastMessagePreview">First line of the most recent message.</param>
/// <param name="LastMessageAt">When the most recent message arrived or was sent.</param>
/// <param name="UnreadCount">Inbound messages nobody has opened.</param>
/// <param name="WindowExpiresAt">
/// When free-form replies stop being allowed, or null once they already have. The client counts down
/// to this and closes its composer when it passes.
/// </param>
public sealed record ConversationResponse(
    string Id,
    string? ContactId,
    string ContactName,
    string PhoneNumber,
    string LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    int UnreadCount,
    DateTimeOffset? WindowExpiresAt);

/// <summary>One message in a thread.</summary>
/// <param name="Id">Public identifier.</param>
/// <param name="Direction">Who sent it.</param>
/// <param name="Kind">What it carries.</param>
/// <param name="Body">Text, or a readable description of what arrived.</param>
/// <param name="Media">Attachment, when there is one.</param>
/// <param name="Status">Delivery state.</param>
/// <param name="FailureReason">Why it failed, when it did.</param>
/// <param name="TemplateName">Template used, for an outbound template send.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record ConversationMessageResponse(
    string Id,
    MessageDirection Direction,
    ConversationMessageKind Kind,
    string Body,
    MediaAssetResponse? Media,
    InboxMessageStatus Status,
    string? FailureReason,
    string? TemplateName,
    DateTimeOffset OccurredAt);

/// <summary>Filters for the conversation list.</summary>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Search">Matches contact name or phone number.</param>
public sealed record ConversationQuery(int Page = 1, int PageSize = 25, string? Search = null);

/// <summary>An agent's reply, sent inside the 24-hour window.</summary>
/// <param name="ConversationId">
/// Thread being replied to. Carried in the body by the client as well as the route; the route wins.
/// </param>
/// <param name="Kind">
/// <c>text</c>, <c>image</c>, <c>video</c>, <c>document</c> or <c>audio</c>. A template send is a
/// campaign, not a reply, and is refused here.
/// </param>
/// <param name="Body">Message text, or a caption for a file.</param>
/// <param name="MediaId">Previously uploaded file, required for every kind but text.</param>
public sealed record SendConversationMessageRequest(
    string? ConversationId,
    ConversationMessageKind Kind,
    string? Body,
    string? MediaId);
