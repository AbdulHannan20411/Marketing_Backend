using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Campaigns;

/// <summary>Delivery counters for a campaign.</summary>
/// <param name="AudienceSize">Contacts in the audience.</param>
/// <param name="Sent">Messages accepted by Meta.</param>
/// <param name="Delivered">Messages confirmed delivered.</param>
/// <param name="Read">Messages opened.</param>
/// <param name="Clicked">Messages whose button or link was clicked.</param>
/// <param name="Failed">Messages that failed.</param>
public sealed record CampaignMetricsResponse(
    int AudienceSize,
    int Sent,
    int Delivered,
    int Read,
    int Clicked,
    int Failed);

/// <summary>A campaign.</summary>
/// <remarks>
/// For a recurring campaign this is the <em>definition</em>, not one send. <see cref="Metrics"/> is
/// then the lifetime total across every run and <see cref="CompletedAt"/> is null, because a
/// campaign that fires again on Monday has not completed. Per-firing counters live on
/// <see cref="CampaignRunResponse"/>.
/// </remarks>
/// <param name="Id">Opaque identifier, prefixed <c>cmp_</c>.</param>
/// <param name="Name">Campaign name.</param>
/// <param name="Description">Free-text description.</param>
/// <param name="TemplateName">Template used.</param>
/// <param name="TemplateId">Template identifier, so the editor need not match by name.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="Metrics">Delivery counters. Lifetime totals when <paramref name="Recurrence"/> is set.</param>
/// <param name="AudienceLabel">Human-readable audience description.</param>
/// <param name="GroupIds">Groups making up the audience, so the editor need not match by label.</param>
/// <param name="Recurrence">Recurrence rule, or null for a one-off.</param>
/// <param name="TimeZone">IANA zone the recurrence is expressed in.</param>
/// <param name="ScheduledAt">Instant a one-off dispatch is scheduled for. Null when recurring.</param>
/// <param name="NextRunAt">Instant the next occurrence is due, in UTC.</param>
/// <param name="LastRunAt">Instant the most recent occurrence fired.</param>
/// <param name="OccurrencesRun">Scheduled firings so far, for an <c>afterCount</c> end condition.</param>
/// <param name="CompletedAt">Instant dispatch finished. Null for a recurring campaign.</param>
/// <param name="CreatedBy">Display name of the creator.</param>
/// <param name="CreatedAt">Instant it was created.</param>
/// <param name="UpdatedAt">Instant it was last modified.</param>
public sealed record CampaignResponse(
    string Id,
    string Name,
    string Description,
    string TemplateName,
    string? TemplateId,
    CampaignStatus Status,
    CampaignMetricsResponse Metrics,
    string AudienceLabel,
    IReadOnlyList<string> GroupIds,
    RecurrenceRule? Recurrence,
    string? TimeZone,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    int OccurrencesRun,
    DateTimeOffset? CompletedAt,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>Delivery counters for one firing.</summary>
/// <param name="AudienceSize">Distinct contacts resolved at fire time.</param>
/// <param name="Sent">Messages accepted by Meta.</param>
/// <param name="Delivered">Messages confirmed delivered.</param>
/// <param name="Read">Messages opened.</param>
/// <param name="Clicked">Messages whose button or link was clicked.</param>
/// <param name="Failed">Messages that failed.</param>
/// <param name="Skipped">Recipients deliberately not messaged - opted out, or over a limit.</param>
public sealed record CampaignRunMetricsResponse(
    int AudienceSize,
    int Sent,
    int Delivered,
    int Read,
    int Clicked,
    int Failed,
    int Skipped);

/// <summary>One firing of a campaign.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>run_</c>.</param>
/// <param name="CampaignId">Campaign this run belongs to.</param>
/// <param name="OccurrenceNumber">1-based position in the series, counting skipped occurrences.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="TriggeredManually">True when started from the run-now action rather than the schedule.</param>
/// <param name="ScheduledFor">Instant this occurrence was due.</param>
/// <param name="StartedAt">Instant dispatch began.</param>
/// <param name="CompletedAt">Instant dispatch finished.</param>
/// <param name="FailureReason">Why it failed or was skipped. Written to be shown to an operator.</param>
/// <param name="Metrics">Counters for this firing alone.</param>
public sealed record CampaignRunResponse(
    string Id,
    string CampaignId,
    int OccurrenceNumber,
    CampaignRunStatus Status,
    bool TriggeredManually,
    DateTimeOffset ScheduledFor,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    CampaignRunMetricsResponse Metrics);

/// <summary>Request to count an audience before committing to it.</summary>
/// <param name="GroupIds">Groups to count across.</param>
public sealed record PreviewAudienceRequest(IReadOnlyList<string> GroupIds);

/// <summary>A deduplicated audience count.</summary>
/// <param name="RecipientCount">Distinct contacts who would receive the campaign.</param>
public sealed record PreviewAudienceResponse(int RecipientCount);

/// <summary>One message that could not be delivered.</summary>
/// <param name="Id">Opaque identifier.</param>
/// <param name="CampaignName">Campaign the message belonged to.</param>
/// <param name="ContactName">Intended recipient.</param>
/// <param name="PhoneNumber">Recipient number.</param>
/// <param name="Reason">Human-readable reason.</param>
/// <param name="ErrorCode">Meta's numeric error code.</param>
/// <param name="OccurredAt">Instant the failure was reported.</param>
public sealed record DeliveryFailureResponse(
    string Id,
    string CampaignName,
    string ContactName,
    string PhoneNumber,
    string Reason,
    int ErrorCode,
    DateTimeOffset OccurredAt);
