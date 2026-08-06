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
/// <param name="Id">Opaque identifier, prefixed <c>cmp_</c>.</param>
/// <param name="Name">Campaign name.</param>
/// <param name="TemplateName">Template used.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="Metrics">Delivery counters.</param>
/// <param name="AudienceLabel">Human-readable audience description.</param>
/// <param name="ScheduledAt">Instant dispatch is scheduled for.</param>
/// <param name="CompletedAt">Instant dispatch finished.</param>
/// <param name="CreatedBy">Display name of the creator.</param>
/// <param name="CreatedAt">Instant it was created.</param>
public sealed record CampaignResponse(
    string Id,
    string Name,
    string TemplateName,
    CampaignStatus Status,
    CampaignMetricsResponse Metrics,
    string AudienceLabel,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? CompletedAt,
    string CreatedBy,
    DateTimeOffset CreatedAt);

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
