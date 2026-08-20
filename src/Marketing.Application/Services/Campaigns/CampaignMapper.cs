using System.Text.Json;
using System.Text.Json.Serialization;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services.Campaigns;

/// <summary>
/// Turns campaign entities into their wire shapes, and the recurrence rule into and out of storage.
/// <para>
/// Shared rather than repeated. The list read, the detail read and every lifecycle transition all
/// return the same record, and three copies of the projection is three places for a new field to be
/// forgotten - which the client experiences as a value that is present on one screen and missing on
/// the next.
/// </para>
/// </summary>
public static class CampaignMapper
{
    /// <summary>
    /// Options for the stored rule.
    /// <para>
    /// Deliberately the same casing the API emits, so the <c>jsonb</c> column reads like the
    /// contract rather than like an internal dump. An operator inspecting the row sees exactly what
    /// the client sent.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions RuleJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Reads a stored rule, treating unreadable JSON as "no recurrence".</summary>
    /// <remarks>
    /// A rule that cannot be parsed must not take the campaign screen down with it. Returning null
    /// renders the campaign as a one-off, which is wrong but legible and fixable; throwing would
    /// make the whole list 500 because one row is bad.
    /// </remarks>
    /// <param name="json">The stored rule, or null.</param>
    public static RecurrenceRule? ReadRecurrence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecurrenceRule>(json, RuleJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialises a rule for storage.</summary>
    /// <param name="rule">The rule, or null for a one-off.</param>
    public static string? WriteRecurrence(RecurrenceRule? rule) =>
        rule is null ? null : JsonSerializer.Serialize(rule, RuleJson);

    /// <summary>Maps a campaign to its wire shape.</summary>
    /// <param name="campaign">The campaign.</param>
    public static CampaignResponse ToResponse(Campaign campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var recurrence = ReadRecurrence(campaign.RecurrenceJson);

        return new CampaignResponse(
            PublicId.From(PublicId.Campaign, campaign.Id),
            campaign.Name,
            campaign.Description,
            campaign.TemplateName,
            campaign.MessageTemplateId is { } templateId
                ? PublicId.From(PublicId.Template, templateId)
                : null,
            campaign.Status,
            new CampaignMetricsResponse(
                campaign.AudienceSize,
                campaign.SentCount,
                campaign.DeliveredCount,
                campaign.ReadCount,
                campaign.ClickedCount,
                campaign.FailedCount),
            campaign.AudienceLabel,
            [.. campaign.AudienceGroupIds.Select(id => PublicId.From(PublicId.Group, id))],
            recurrence,
            campaign.TimeZone,
            // A recurring campaign has no single scheduled instant, and returning one would have the
            // client render a fixed date for something that repeats.
            recurrence is null ? campaign.ScheduledAt : null,
            campaign.NextRunAtUtc,
            campaign.LastRunAtUtc,
            campaign.OccurrencesRun,
            // Likewise: a campaign that fires again on Monday has not completed, whatever the last
            // run did.
            recurrence is null ? campaign.CompletedAt : null,
            campaign.CreatedByName,
            campaign.CreatedOn,
            campaign.ModifiedOn);
    }

    /// <summary>Maps one firing to its wire shape.</summary>
    /// <param name="run">The run.</param>
    public static CampaignRunResponse ToResponse(CampaignRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new CampaignRunResponse(
            PublicId.From(PublicId.CampaignRun, run.Id),
            PublicId.From(PublicId.Campaign, run.CampaignId),
            run.OccurrenceNumber,
            run.Status,
            run.TriggeredManually,
            run.ScheduledForUtc,
            run.StartedAt,
            run.CompletedAt,
            run.FailureReason,
            new CampaignRunMetricsResponse(
                run.AudienceSize,
                run.SentCount,
                run.DeliveredCount,
                run.ReadCount,
                run.ClickedCount,
                run.FailedCount,
                run.SkippedCount));
    }
}
