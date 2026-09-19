namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>One row of the auto-reply knowledge file.</summary>
/// <param name="Kind"><c>business</c>, <c>faq</c>, <c>product</c>, <c>policy</c> or <c>rule</c>.</param>
/// <param name="Title">What the fact is, the question, the product, the policy or the rule.</param>
/// <param name="Answer">The fact, answer, description or policy. Optional only for a rule.</param>
/// <param name="Price">Price as written. Products only; null for every other kind.</param>
/// <param name="Available">Whether it can be ordered. Products only; null when not stated.</param>
/// <param name="Keywords">Other ways customers ask for it. May be empty.</param>
public sealed record KnowledgeEntryDto(
    string? Kind,
    string? Title,
    string? Answer,
    string? Price,
    bool? Available,
    IReadOnlyList<string>? Keywords);

/// <summary>The workspace's knowledge file and what to do when it has no answer.</summary>
/// <param name="Entries">Every entry, in upload order.</param>
/// <param name="Fallback"><c>handoff</c> or <c>silent</c>.</param>
/// <param name="FallbackMessage">The holding message a handoff sends.</param>
/// <param name="SourceFileName">The uploaded file's name, or null if nothing was ever uploaded.</param>
/// <param name="UpdatedAt">When it was last replaced, or null.</param>
/// <param name="UpdatedByName">Who replaced it, or null.</param>
public sealed record AutoReplyKnowledgeResponse(
    IReadOnlyList<KnowledgeEntryDto> Entries,
    string Fallback,
    string FallbackMessage,
    string? SourceFileName,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByName);

/// <summary>Replaces the knowledge file wholesale.</summary>
/// <param name="Entries">0 to 500 entries.</param>
/// <param name="Fallback"><c>handoff</c> or <c>silent</c>.</param>
/// <param name="FallbackMessage">Required, 1-300 characters, when the fallback is a handoff.</param>
/// <param name="SourceFileName">The uploaded file's name, for display. Any path is stripped.</param>
public sealed record AutoReplyKnowledgeRequest(
    IReadOnlyList<KnowledgeEntryDto>? Entries,
    string? Fallback,
    string? FallbackMessage,
    string? SourceFileName);
