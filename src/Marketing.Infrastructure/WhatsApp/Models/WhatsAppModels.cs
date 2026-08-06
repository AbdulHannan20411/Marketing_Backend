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
public sealed record WhatsAppPhoneNumber(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_phone_number")] string DisplayPhoneNumber,
    [property: JsonPropertyName("verified_name")] string? VerifiedName,
    [property: JsonPropertyName("quality_rating")] string? QualityRating,
    [property: JsonPropertyName("code_verification_status")] string? CodeVerificationStatus);

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
