using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Campaigns;

/// <summary>Request to create or replace a contact group.</summary>
/// <param name="Name">Group name.</param>
/// <param name="Description">Description.</param>
public sealed record ContactGroupDraft(string Name, string Description = "");

/// <summary>Request to create or replace a tag.</summary>
/// <param name="Name">Tag name.</param>
/// <param name="Color">Badge colour.</param>
public sealed record ContactTagDraft(string Name, TagColor Color = TagColor.Neutral);

/// <summary>Request to create or replace a message template.</summary>
/// <param name="Name">Template name.</param>
/// <param name="Category">Meta category.</param>
/// <param name="Language">BCP 47 language tag.</param>
/// <param name="HeaderText">Optional header.</param>
/// <param name="BodyText">Body, with <c>{{1}}</c> placeholders.</param>
/// <param name="FooterText">Optional footer.</param>
/// <param name="Variables">
/// Ignored. Kept so older clients still bind; the placeholders are read from the body instead.
/// </param>
/// <param name="Buttons">
/// Buttons as the editor sends them - kind, label and value - or as bare labels, which are quick replies.
/// </param>
/// <param name="HeaderKind">
/// <c>none</c>, <c>text</c>, <c>image</c>, <c>video</c> or <c>document</c>. When absent it is read from
/// whether header text was given.
/// </param>
/// <param name="BodyExamples">
/// One real example per body placeholder, <c>{{1}}</c> first. Meta rejects vague examples, so these
/// are what it reviews. When absent, as from older clients, each is sent as "Sample n".
/// </param>
/// <param name="HeaderExample">The example for a text header's <c>{{1}}</c>; empty when it has none.</param>
/// <param name="HeaderSampleId">
/// The uploaded example file, <c>tsm_…</c>. Required for an image, video or document header.
/// </param>
public sealed record MessageTemplateDraft(
    string Name,
    TemplateCategory Category,
    string Language,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string>? Variables = null,
    IReadOnlyList<TemplateButtonDraft>? Buttons = null,
    string? HeaderKind = null,
    IReadOnlyList<string>? BodyExamples = null,
    string? HeaderExample = null,
    string? HeaderSampleId = null);

/// <summary>Request to create or replace a campaign.</summary>
/// <param name="Name">Campaign name.</param>
/// <param name="TemplateId">Template to send.</param>
/// <param name="AudienceLabel">Human-readable audience description.</param>
/// <param name="GroupIds">Groups making up the audience.</param>
/// <param name="Description">Free-text description.</param>
/// <param name="WhatsAppAccountId">
/// The number that sends it, <c>wa_…</c>. Required once the workspace has more than one.
/// </param>
/// <param name="HeaderMediaId">
/// The image, video or document sent in every message's header, <c>med_…</c> from
/// <c>POST /whatsapp/media</c>. Required when the template has a media header; refused otherwise.
/// </param>
public sealed record CampaignDraft(
    string Name,
    string TemplateId,
    string AudienceLabel = "",
    IReadOnlyList<string>? GroupIds = null,
    string Description = "",
    string? WhatsAppAccountId = null,
    string? HeaderMediaId = null);

/// <summary>
/// Request to schedule a campaign, as a one-off instant or as a repeating rule.
/// </summary>
/// <remarks>
/// Both members are optional and the client sends both today. <see cref="Recurrence"/> wins when it
/// is present and describes anything other than a single occurrence; a <c>once</c> rule also wins,
/// because it carries the timezone and <see cref="ScheduledAt"/> does not. A request carrying
/// neither is refused rather than silently scheduling nothing.
/// </remarks>
/// <param name="ScheduledAt">When to dispatch a one-off. Must be in the future.</param>
/// <param name="Recurrence">How the campaign repeats.</param>
public sealed record ScheduleCampaignRequest(
    DateTimeOffset? ScheduledAt = null,
    RecurrenceRule? Recurrence = null);
