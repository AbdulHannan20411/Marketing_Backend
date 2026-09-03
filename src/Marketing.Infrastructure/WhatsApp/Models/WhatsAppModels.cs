using System.Text.Json.Serialization;

namespace Marketing.Infrastructure.WhatsApp.Models;

/// <summary>Graph API envelope for one page of a collection.</summary>
/// <typeparam name="TItem">Item type.</typeparam>
/// <param name="Data">Items on this page.</param>
/// <param name="Paging">Cursors for the adjacent pages.</param>
public sealed record GraphPage<TItem>(
    [property: JsonPropertyName("data")] IReadOnlyList<TItem> Data,
    [property: JsonPropertyName("paging")] GraphPaging? Paging);

/// <summary>Graph API cursor pair.</summary>
/// <param name="Cursors">Before and after cursors.</param>
/// <param name="Next">Absolute URL of the next page, when one exists.</param>
public sealed record GraphPaging(
    [property: JsonPropertyName("cursors")] GraphCursors? Cursors,
    [property: JsonPropertyName("next")] string? Next);

/// <summary>Graph API cursors.</summary>
/// <param name="Before">Cursor for the previous page.</param>
/// <param name="After">Cursor for the next page.</param>
public sealed record GraphCursors(
    [property: JsonPropertyName("before")] string? Before,
    [property: JsonPropertyName("after")] string? After);

/// <summary>A phone number registered against a WhatsApp Business Account.</summary>
/// <param name="Id">Phone number identifier used when sending messages.</param>
/// <param name="DisplayPhoneNumber">Number in international display format.</param>
/// <param name="VerifiedName">Business name Meta has verified for the number.</param>
/// <param name="QualityRating">Meta's current quality rating: GREEN, YELLOW or RED.</param>
/// <param name="CodeVerificationStatus">Whether the number has completed verification.</param>
/// <param name="MessagingTier">
/// Daily unique-customer ceiling, as <c>TIER_1K</c> and similar. Meta omits it on numbers it has
/// not yet tiered.
/// </param>
public sealed record WhatsAppPhoneNumber(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_phone_number")] string DisplayPhoneNumber,
    [property: JsonPropertyName("verified_name")] string? VerifiedName,
    [property: JsonPropertyName("quality_rating")] string? QualityRating,
    [property: JsonPropertyName("code_verification_status")] string? CodeVerificationStatus,
    [property: JsonPropertyName("messaging_limit_tier")] string? MessagingTier = null);

/// <summary>A message template as Meta holds it.</summary>
/// <param name="Id">Template identifier.</param>
/// <param name="Name">Template name.</param>
/// <param name="Language">BCP 47 language tag.</param>
/// <param name="Status">Review status: APPROVED, PENDING, REJECTED or PAUSED.</param>
/// <param name="Category">Template category: MARKETING, UTILITY or AUTHENTICATION.</param>
public sealed record WhatsAppTemplate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("category")] string Category);

/// <summary>Result of a send request.</summary>
/// <param name="MessagingProduct">Always <c>whatsapp</c>.</param>
/// <param name="Messages">Identifiers Meta assigned to the accepted messages.</param>
public sealed record SendMessageResponse(
    [property: JsonPropertyName("messaging_product")] string MessagingProduct,
    [property: JsonPropertyName("messages")] IReadOnlyList<SentMessage> Messages);

/// <summary>An accepted outbound message.</summary>
/// <param name="Id">
/// Meta's message identifier. Delivery and read receipts arrive later on the webhook keyed by this
/// value, so it is the correlation key for the entire message lifecycle.
/// </param>
public sealed record SentMessage([property: JsonPropertyName("id")] string Id);

/// <summary>Error body Meta returns on a failed Graph API call.</summary>
/// <param name="Error">Error detail.</param>
public sealed record GraphErrorResponse([property: JsonPropertyName("error")] GraphError? Error);

/// <summary>Graph API error detail.</summary>
/// <param name="Message">Human-readable message.</param>
/// <param name="Type">Error type.</param>
/// <param name="Code">Numeric error code.</param>
/// <param name="SubCode">Numeric sub-code, when present.</param>
/// <param name="TraceId">Meta's trace identifier - quote it when raising a support case.</param>
public sealed record GraphError(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("error_subcode")] int? SubCode,
    [property: JsonPropertyName("fbtrace_id")] string? TraceId);

/// <summary>Result of exchanging an Embedded Signup code.</summary>
/// <param name="AccessToken">
/// The business access token. A live credential - it is encrypted before it touches the database
/// and never appears in a DTO, a log line or an audit row.
/// </param>
/// <param name="TokenType">Token type, always <c>bearer</c>.</param>
/// <param name="ExpiresIn">Lifetime in seconds. Absent for long-lived system-user tokens.</param>
public sealed record TokenExchangeResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] long? ExpiresIn);

/// <summary>Meta's verdict on a token, from <c>/debug_token</c>.</summary>
/// <param name="Data">The inspection result.</param>
public sealed record TokenDebugResponse(
    [property: JsonPropertyName("data")] TokenDebugData? Data);

/// <summary>What Meta knows about a token.</summary>
/// <param name="IsValid">Whether Meta will currently accept it.</param>
/// <param name="Type">Token kind - <c>USER</c>, <c>SYSTEM_USER</c>, <c>PAGE</c>.</param>
/// <param name="ExpiresAt">
/// Unix seconds at which the token stops working. Meta sends <c>0</c> for a token that never
/// expires, which is why this is read as "0 means none" rather than as the epoch.
/// </param>
public sealed record TokenDebugData(
    [property: JsonPropertyName("is_valid")] bool IsValid,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("expires_at")] long? ExpiresAt);

/// <summary>A template message to send, in the shape the Cloud API expects.</summary>
/// <param name="To">Recipient in E.164 without the leading plus.</param>
/// <param name="Template">Template name, language and components.</param>
public sealed record SendTemplateMessageRequest(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("template")] TemplateMessagePayload Template)
{
    /// <summary>Always <c>whatsapp</c>; Meta rejects the request without it.</summary>
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; } = "whatsapp";

    /// <summary>Always <c>template</c> for campaign sends.</summary>
    [JsonPropertyName("type")]
    public string Type { get; } = "template";
}

/// <summary>Template selection and its variable bindings.</summary>
/// <param name="Name">Template name as registered with Meta.</param>
/// <param name="Language">Language tag wrapper.</param>
/// <param name="Components">Variable bindings, omitted when the template has no placeholders.</param>
public sealed record TemplateMessagePayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("language")] TemplateLanguage Language,

    // Omitted entirely when there is nothing to substitute, not serialised as null. A template
    // with no placeholders - hello_world, for instance - is refused with "parameter format does
    // not match" if the key is present and empty, because Meta reads its presence as a promise
    // that parameters follow. The default serialiser writes nulls, so this has to say otherwise.
    [property: JsonPropertyName("components")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<TemplateComponent>? Components);

/// <summary>Template language selector.</summary>
/// <param name="Code">BCP 47 language tag, for example <c>en_GB</c>.</param>
public sealed record TemplateLanguage([property: JsonPropertyName("code")] string Code);

/// <summary>One component of a template message.</summary>
/// <param name="Type">Component type, for example <c>body</c>.</param>
/// <param name="Parameters">Ordered parameter values filling the placeholders.</param>
public sealed record TemplateComponent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("parameters")] IReadOnlyList<TemplateParameter> Parameters);

/// <summary>One template parameter value.</summary>
/// <param name="Text">The substituted text.</param>
public sealed record TemplateParameter([property: JsonPropertyName("text")] string Text)
{
    /// <summary>Always <c>text</c>; media parameters are not used by campaigns yet.</summary>
    [JsonPropertyName("type")]
    public string Type { get; } = "text";
}

/// <summary>
/// Graph's acknowledgement of a write it has nothing else to say about.
/// </summary>
/// <remarks>
/// Meta answers <c>{"success": true}</c> to several calls. Modelled rather than ignored so a
/// response of <c>false</c> is visible to the caller instead of being read as success by silence.
/// </remarks>
/// <param name="Success">Whether the call was accepted.</param>
public sealed record GraphSuccess([property: JsonPropertyName("success")] bool Success);

/// <summary>A WhatsApp Business Account.</summary>
/// <param name="Id">Account identifier.</param>
/// <param name="Name">Business name.</param>
/// <param name="TemplateNamespace">Namespace templates are published under.</param>
/// <param name="ReviewStatus">Where Meta's review of the account stands.</param>
public sealed record WhatsAppBusinessAccount(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("message_template_namespace")] string? TemplateNamespace,
    [property: JsonPropertyName("account_review_status")] string? ReviewStatus);

/// <summary>The handle returned by a media upload.</summary>
/// <param name="Id">Media identifier a send refers to.</param>
public sealed record MediaUploadResponse([property: JsonPropertyName("id")] string Id);

/// <summary>
/// Where a media id's bytes can be read from.
/// </summary>
/// <remarks>
/// The URL is short-lived and needs the business token as a bearer, which is why media is fetched
/// server-side and re-served from our own storage rather than linked to directly.
/// </remarks>
/// <param name="Url">Temporary download URL.</param>
/// <param name="MimeType">Media type.</param>
/// <param name="FileSize">Size in bytes.</param>
/// <param name="Id">Media identifier.</param>
public sealed record MediaHandle(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("mime_type")] string? MimeType,
    [property: JsonPropertyName("file_size")] long FileSize,
    [property: JsonPropertyName("id")] string? Id);

/// <summary>What Meta returns when a template is created.</summary>
/// <param name="Id">Meta's identifier for the template.</param>
/// <param name="Status">Review status, normally <c>PENDING</c>.</param>
/// <param name="Category">
/// The category Meta assigned. Meta categorises on content, so this can differ from the category
/// submitted — the caller stores what came back, not what was asked for.
/// </param>
public sealed record TemplateMutationResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("category")] string? Category);
