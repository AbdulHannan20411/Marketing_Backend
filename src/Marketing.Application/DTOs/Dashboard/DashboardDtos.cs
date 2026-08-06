namespace Marketing.Application.DTOs.Dashboard;

/// <summary>Everything the dashboard renders, for the last 30 days.</summary>
/// <param name="Kpis">Headline counters and their deltas.</param>
/// <param name="Trend">Thirty daily points, oldest first.</param>
/// <param name="Funnel">Sent, Delivered, Read, Clicked.</param>
/// <param name="Activity">Recent actions.</param>
public sealed record DashboardSnapshot(
    KpiSummary Kpis,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<FunnelStage> Funnel,
    IReadOnlyList<ActivityEntryResponse> Activity);

/// <summary>Headline counters, with the change against the preceding 30 days.</summary>
/// <param name="MessagesSent">Messages accepted by Meta.</param>
/// <param name="Delivered">Messages confirmed delivered.</param>
/// <param name="Read">Messages opened.</param>
/// <param name="Failed">Messages that failed.</param>
/// <param name="ClickThroughRate">Click-through rate, where 15.7 means 15.7 per cent.</param>
/// <param name="MessagesSentDelta">Percentage change against the previous period; may be negative.</param>
/// <param name="DeliveredDelta">Percentage change against the previous period.</param>
/// <param name="ReadDelta">Percentage change against the previous period.</param>
/// <param name="FailedDelta">Percentage change against the previous period.</param>
/// <param name="ClickThroughRateDelta">Percentage-point change against the previous period.</param>
public sealed record KpiSummary(
    int MessagesSent,
    int Delivered,
    int Read,
    int Failed,
    decimal ClickThroughRate,
    decimal MessagesSentDelta,
    decimal DeliveredDelta,
    decimal ReadDelta,
    decimal FailedDelta,
    decimal ClickThroughRateDelta);

/// <summary>One day on the trend chart.</summary>
/// <param name="Date">
/// Date only, serialised as <c>YYYY-MM-DD</c>. The one exception to the ISO 8601 instant rule,
/// because the client uses it as a chart axis label rather than parsing it as a moment in time.
/// </param>
/// <param name="Sent">Messages sent that day.</param>
/// <param name="Delivered">Messages delivered that day.</param>
/// <param name="Read">Messages read that day.</param>
public sealed record TrendPoint(DateOnly Date, int Sent, int Delivered, int Read);

/// <summary>One stage of the delivery funnel.</summary>
/// <param name="Label">Stage name.</param>
/// <param name="Value">Count at this stage.</param>
public sealed record FunnelStage(string Label, int Value);

/// <summary>One entry on the activity feed.</summary>
/// <param name="Id">Entry identifier.</param>
/// <param name="Actor">Who acted.</param>
/// <param name="ActorInitials">Initials, computed server-side so every client renders them alike.</param>
/// <param name="Action">Verb phrase, for example "launched campaign".</param>
/// <param name="Subject">What was acted on.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record ActivityEntryResponse(
    string Id,
    string Actor,
    string ActorInitials,
    string Action,
    string Subject,
    DateTimeOffset OccurredAt);
