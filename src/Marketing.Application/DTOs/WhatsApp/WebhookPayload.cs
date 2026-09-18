using System.Text.Json.Serialization;

namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>
/// The envelope Meta posts to the webhook.
/// <para>
/// Every member is nullable and every collection defaulted. This payload comes from outside the
/// trust boundary and Meta adds fields between Graph versions, so the parser's job is to survive
/// anything and extract what it recognises - not to insist the body matches a fixed shape.
/// </para>
/// </summary>
/// <param name="ObjectType">Subscription object, <c>whatsapp_business_account</c>.</param>
/// <param name="Entry">One entry per business account.</param>
public sealed record WebhookEnvelope(
    [property: JsonPropertyName("object")] string? ObjectType,
    [property: JsonPropertyName("entry")] IReadOnlyList<WebhookEntry>? Entry);

/// <summary>One business account's changes.</summary>
/// <param name="Id">WhatsApp Business Account identifier.</param>
/// <param name="Changes">Changes reported for it.</param>
public sealed record WebhookEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("changes")] IReadOnlyList<WebhookChange>? Changes);

/// <summary>One change within an entry.</summary>
/// <param name="Field">Change type, for example <c>messages</c>.</param>
/// <param name="Value">Change body.</param>
public sealed record WebhookChange(
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("value")] WebhookValue? Value);

/// <summary>Body of a change.</summary>
/// <param name="Metadata">Which number the change concerns.</param>
/// <param name="Statuses">Delivery receipts.</param>
/// <param name="TemplateEvent">Template review outcome, on a template status change.</param>
/// <param name="TemplateName">Template the review outcome concerns.</param>
/// <param name="TemplateLanguage">Language of that template.</param>
/// <param name="TemplateRejectionReason">Why Meta rejected it.</param>
/// <param name="Messages">Messages a customer sent.</param>
/// <param name="Contacts">Who sent them, with the profile names Meta reports.</param>
/// <param name="NewCategory">Category Meta has moved a template to.</param>
/// <param name="PreviousCategory">Category it held before.</param>
/// <param name="CurrentLimit">Messaging tier Meta now allows the number.</param>
public sealed record WebhookValue(
    [property: JsonPropertyName("metadata")] WebhookMetadata? Metadata,
    [property: JsonPropertyName("statuses")] IReadOnlyList<WebhookStatus>? Statuses,
    [property: JsonPropertyName("event")] string? TemplateEvent,
    [property: JsonPropertyName("message_template_name")] string? TemplateName,
    [property: JsonPropertyName("message_template_language")] string? TemplateLanguage,
    [property: JsonPropertyName("reason")] string? TemplateRejectionReason,
    [property: JsonPropertyName("messages")] IReadOnlyList<WebhookInboundMessage>? Messages = null,
    [property: JsonPropertyName("contacts")] IReadOnlyList<WebhookContact>? Contacts = null,
    [property: JsonPropertyName("new_category")] string? NewCategory = null,
    [property: JsonPropertyName("previous_category")] string? PreviousCategory = null,
    [property: JsonPropertyName("current_limit")] string? CurrentLimit = null);

/// <summary>One message a customer sent.</summary>
/// <param name="Id">Meta's message identifier, unique and the key against redelivery.</param>
/// <param name="From">Sender's WhatsApp number.</param>
/// <param name="Timestamp">Unix seconds, as a string.</param>
/// <param name="Type">Message type: <c>text</c>, <c>image</c>, <c>interactive</c> and the rest.</param>
/// <param name="Text">Body, for a text message.</param>
/// <param name="Image">Attachment, for an image.</param>
/// <param name="Video">Attachment, for a video.</param>
/// <param name="Document">Attachment, for a file.</param>
/// <param name="Audio">Attachment, for a voice note or audio file.</param>
/// <param name="Button">What was tapped, for a quick-reply button on a template.</param>
/// <param name="Interactive">What was chosen, for a button or list reply.</param>
public sealed record WebhookInboundMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] WebhookTextPayload? Text = null,
    [property: JsonPropertyName("image")] WebhookMediaPayload? Image = null,
    [property: JsonPropertyName("video")] WebhookMediaPayload? Video = null,
    [property: JsonPropertyName("document")] WebhookMediaPayload? Document = null,
    [property: JsonPropertyName("audio")] WebhookMediaPayload? Audio = null,
    [property: JsonPropertyName("button")] WebhookButtonPayload? Button = null,
    [property: JsonPropertyName("interactive")] WebhookInteractivePayload? Interactive = null);

/// <summary>The text of a message.</summary>
/// <param name="Body">What was written.</param>
public sealed record WebhookTextPayload([property: JsonPropertyName("body")] string? Body);

/// <summary>An attachment Meta is holding for 30 days.</summary>
/// <param name="Id">Media identifier, which the bytes are fetched with.</param>
/// <param name="MimeType">Media type.</param>
/// <param name="Caption">Caption the customer typed, where the type allows one.</param>
/// <param name="FileName">Original file name, for a document.</param>
public sealed record WebhookMediaPayload(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("mime_type")] string? MimeType = null,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("filename")] string? FileName = null);

/// <summary>A quick-reply button on a template, as tapped.</summary>
/// <param name="Text">Button label.</param>
/// <param name="Payload">Value the template attached to it.</param>
public sealed record WebhookButtonPayload(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("payload")] string? Payload = null);

/// <summary>A reply to an interactive message.</summary>
/// <param name="Type">Which kind of reply it is.</param>
/// <param name="ButtonReply">The button chosen.</param>
/// <param name="ListReply">The list item chosen.</param>
public sealed record WebhookInteractivePayload(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("button_reply")] WebhookReplyPayload? ButtonReply = null,
    [property: JsonPropertyName("list_reply")] WebhookReplyPayload? ListReply = null);

/// <summary>One choice from an interactive message.</summary>
/// <param name="Id">Identifier the template gave the option.</param>
/// <param name="Title">What the customer saw and chose.</param>
public sealed record WebhookReplyPayload(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title = null);

/// <summary>Who sent the messages in this change.</summary>
/// <param name="WaId">Their WhatsApp number.</param>
/// <param name="Profile">Their WhatsApp profile.</param>
public sealed record WebhookContact(
    [property: JsonPropertyName("wa_id")] string? WaId,
    [property: JsonPropertyName("profile")] WebhookProfile? Profile = null);

/// <summary>A customer's WhatsApp profile.</summary>
/// <param name="Name">The name they set, which is not a name the business chose.</param>
public sealed record WebhookProfile([property: JsonPropertyName("name")] string? Name);

/// <summary>Identifies the number a change concerns.</summary>
/// <param name="DisplayPhoneNumber">Number in display format.</param>
/// <param name="PhoneNumberId">Phone number identifier, which resolves the tenant.</param>
public sealed record WebhookMetadata(
    [property: JsonPropertyName("display_phone_number")] string? DisplayPhoneNumber,
    [property: JsonPropertyName("phone_number_id")] string? PhoneNumberId);

/// <summary>One delivery receipt.</summary>
/// <param name="MessageId">Meta's message identifier, matching a sent message.</param>
/// <param name="Status">New status: <c>sent</c>, <c>delivered</c>, <c>read</c> or <c>failed</c>.</param>
/// <param name="Timestamp">Unix seconds, as a string.</param>
/// <param name="RecipientId">Recipient's number.</param>
/// <param name="Errors">Failure details, present when the status is <c>failed</c>.</param>
public sealed record WebhookStatus(
    [property: JsonPropertyName("id")] string? MessageId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("recipient_id")] string? RecipientId,
    [property: JsonPropertyName("errors")] IReadOnlyList<WebhookError>? Errors);

/// <summary>Why a message failed.</summary>
/// <param name="Code">Meta's numeric error code.</param>
/// <param name="Title">Short description.</param>
/// <param name="Message">Longer description, when Meta supplies one.</param>
public sealed record WebhookError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("message")] string? Message);
