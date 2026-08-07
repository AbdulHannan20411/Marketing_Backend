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
public sealed record WebhookValue(
    [property: JsonPropertyName("metadata")] WebhookMetadata? Metadata,
    [property: JsonPropertyName("statuses")] IReadOnlyList<WebhookStatus>? Statuses,
    [property: JsonPropertyName("event")] string? TemplateEvent,
    [property: JsonPropertyName("message_template_name")] string? TemplateName,
    [property: JsonPropertyName("message_template_language")] string? TemplateLanguage,
    [property: JsonPropertyName("reason")] string? TemplateRejectionReason);

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
