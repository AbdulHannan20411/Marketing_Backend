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
/// <param name="Variables">Ordered variable names.</param>
/// <param name="Buttons">Button labels.</param>
public sealed record MessageTemplateDraft(
    string Name,
    TemplateCategory Category,
    string Language,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string>? Variables = null,
    IReadOnlyList<string>? Buttons = null);

/// <summary>Request to create or replace a campaign.</summary>
/// <param name="Name">Campaign name.</param>
/// <param name="TemplateId">Template to send.</param>
/// <param name="AudienceLabel">Human-readable audience description.</param>
/// <param name="GroupIds">Groups making up the audience.</param>
public sealed record CampaignDraft(
    string Name,
    string TemplateId,
    string AudienceLabel = "",
    IReadOnlyList<string>? GroupIds = null);

/// <summary>Request to schedule a campaign.</summary>
/// <param name="ScheduledAt">When to dispatch. Must be in the future.</param>
public sealed record ScheduleCampaignRequest(DateTimeOffset ScheduledAt);
