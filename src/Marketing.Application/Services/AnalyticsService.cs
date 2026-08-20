using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Services.Campaigns;
using Marketing.Common.Exceptions;
using Marketing.Application.DTOs.Dashboard;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IAnalyticsService" />
public sealed class AnalyticsService : IAnalyticsService
{
    /// <summary>Window the dashboard reports on.</summary>
    private const int WindowDays = 30;

    /// <summary>Activity entries shown on the feed.</summary>
    private const int ActivityLimit = 12;

    private readonly IRepository<MessageDailyStat> _stats;
    private readonly IRepository<ActivityEntry> _activity;
    private readonly IRepository<DeliveryFailure> _failures;
    private readonly IQueryExecutor _queries;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public AnalyticsService(
        IRepository<MessageDailyStat> stats,
        IRepository<ActivityEntry> activity,
        IRepository<DeliveryFailure> failures,
        IQueryExecutor queries,
        IDateTimeProvider clock)
    {
        _stats = stats;
        _activity = activity;
        _failures = failures;
        _queries = queries;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var windowStart = today.AddDays(-(WindowDays - 1));

        // The previous window is fetched in the same query so the deltas cost one round trip
        // rather than two.
        var comparisonStart = windowStart.AddDays(-WindowDays);

        var rows = await _queries.ToListAsync(
            _stats.Query()
                .Where(stat => stat.Date >= comparisonStart && stat.Date <= today)
                .OrderBy(stat => stat.Date)
                .Select(stat => new StatRow(
                    stat.Date, stat.Sent, stat.Delivered, stat.Read, stat.Clicked, stat.Failed)),
            cancellationToken);

        var current = rows.Where(row => row.Date >= windowStart).ToList();
        var previous = rows.Where(row => row.Date < windowStart).ToList();

        var activity = await _queries.ToListAsync(
            _activity.Query()
                .OrderByDescending(entry => entry.OccurredOn)
                .Take(ActivityLimit)
                .Select(entry => new ActivityRow(entry.Id, entry.Actor, entry.Action, entry.Subject, entry.OccurredOn)),
            cancellationToken);

        return new DashboardSnapshot(
            BuildKpis(current, previous),
            BuildTrend(current, windowStart, today),
            BuildFunnel(current),
            [.. activity.Select(row => new ActivityEntryResponse(
                PublicId.From(PublicId.Audit, row.Id),
                row.Actor,
                Initials.From(row.Actor),
                row.Action,
                row.Subject,
                row.OccurredOn))]);
    }

    /// <inheritdoc />
    public async Task<PagedResult<DeliveryFailureResponse>> GetFailuresAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projected = _failures.Query()
            .OrderByDescending(failure => failure.OccurredOn)
            .Select(failure => new
            {
                failure.Id,
                failure.CampaignName,
                failure.ContactName,
                failure.PhoneNumber,
                failure.Reason,
                failure.ErrorCode,
                failure.OccurredOn,
            });

        var page = await _queries.ToPagedAsync(projected, request.PageNumber, request.PageSize, cancellationToken);

        return page.Map(row => new DeliveryFailureResponse(
            PublicId.From(PublicId.DeliveryFailure, row.Id),
            row.CampaignName,
            row.ContactName,
            row.PhoneNumber,
            row.Reason,
            row.ErrorCode,
            row.OccurredOn));
    }

    private static KpiSummary BuildKpis(List<StatRow> current, List<StatRow> previous)
    {
        var sent = current.Sum(row => row.Sent);
        var delivered = current.Sum(row => row.Delivered);
        var read = current.Sum(row => row.Read);
        var clicked = current.Sum(row => row.Clicked);
        var failed = current.Sum(row => row.Failed);

        var previousSent = previous.Sum(row => row.Sent);
        var previousClicked = previous.Sum(row => row.Clicked);

        var clickThrough = Rate(clicked, delivered);
        var previousClickThrough = Rate(previousClicked, previous.Sum(row => row.Delivered));

        return new KpiSummary(
            sent,
            delivered,
            read,
            failed,
            clickThrough,
            PercentChange(sent, previousSent),
            PercentChange(delivered, previous.Sum(row => row.Delivered)),
            PercentChange(read, previous.Sum(row => row.Read)),
            PercentChange(failed, previous.Sum(row => row.Failed)),
            // A rate delta is a difference in percentage points, not a percentage change of a
            // percentage - the latter is a number nobody can interpret.
            Math.Round(clickThrough - previousClickThrough, 1));
    }

    /// <summary>
    /// Produces exactly one point per day across the window.
    /// <para>
    /// Days with no activity have no stored row, so they are filled with zeroes. Without this the
    /// chart would silently compress a quiet week into a shorter axis.
    /// </para>
    /// </summary>
    private static List<TrendPoint> BuildTrend(List<StatRow> current, DateOnly from, DateOnly to)
    {
        var byDate = current.ToDictionary(row => row.Date);
        var points = new List<TrendPoint>(WindowDays);

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            points.Add(byDate.TryGetValue(date, out var row)
                ? new TrendPoint(date, row.Sent, row.Delivered, row.Read)
                : new TrendPoint(date, 0, 0, 0));
        }

        return points;
    }

    private static List<FunnelStage> BuildFunnel(List<StatRow> current) =>
    [
        new("Sent", current.Sum(row => row.Sent)),
        new("Delivered", current.Sum(row => row.Delivered)),
        new("Read", current.Sum(row => row.Read)),
        new("Clicked", current.Sum(row => row.Clicked)),
    ];

    private static decimal Rate(int numerator, int denominator) =>
        denominator == 0 ? 0m : Math.Round(numerator * 100m / denominator, 1);

    /// <summary>
    /// Percentage change against the previous window.
    /// <para>
    /// A previous value of zero yields zero rather than infinity: "up ∞ per cent" is not something
    /// a dashboard can render, and the first period of any new tenant would hit it.
    /// </para>
    /// </summary>
    private static decimal PercentChange(int current, int previous) =>
        previous == 0 ? 0m : Math.Round((current - previous) * 100m / previous, 1);

    private sealed record StatRow(DateOnly Date, int Sent, int Delivered, int Read, int Clicked, int Failed);

    private sealed record ActivityRow(long Id, string Actor, string Action, string Subject, DateTimeOffset OccurredOn);
}

/// <inheritdoc cref="ICampaignService" />
public sealed class CampaignService : ICampaignService
{
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<CampaignRun> _runs;
    private readonly IRepository<CampaignMessage> _messages;
    private readonly IQueryExecutor _queries;

    /// <summary>Initialises a new instance.</summary>
    public CampaignService(
        IRepository<Campaign> campaigns,
        IRepository<CampaignRun> runs,
        IRepository<CampaignMessage> messages,
        IQueryExecutor queries)
    {
        _campaigns = campaigns;
        _runs = runs;
        _messages = messages;
        _queries = queries;
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> GetCampaignAsync(
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Campaign, campaignId, "campaign");

        var campaign = await _queries.FirstOrDefaultAsync(
            _campaigns.Query().Where(candidate => candidate.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Campaign", campaignId);

        return CampaignMapper.ToResponse(campaign);
    }

    /// <inheritdoc />
    public async Task<PagedResult<CampaignRunResponse>> GetRunsAsync(
        string campaignId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Campaign, campaignId, "campaign");

        // Ordered by the instant the occurrence was due rather than by when it was written, so a
        // manual run started before an overdue scheduled one still reads in schedule order.
        var query = _runs.Query()
            .Where(run => run.CampaignId == id)
            .OrderByDescending(run => run.ScheduledForUtc)
            .ThenByDescending(run => run.OccurrenceNumber);

        var page = await _queries.ToPagedAsync(query, request.Page, request.PageSize, cancellationToken);

        // Counters are rolled onto the run when it closes, so a run still in flight would otherwise
        // read as all zeroes while it is the one the operator is actually watching. Recounted here
        // from the message rows, which are the only record that cannot be stale.
        var live = page.Items
            .Where(run => run.Status is CampaignRunStatus.Pending or CampaignRunStatus.Running)
            .Select(run => run.Id)
            .ToList();

        if (live.Count > 0)
        {
            var counts = await _queries.ToListAsync(
                _messages.Query()
                    .Where(message => message.CampaignRunId != null && live.Contains(message.CampaignRunId.Value))
                    .GroupBy(message => new { RunId = message.CampaignRunId!.Value, message.Status })
                    .Select(group => new { group.Key.RunId, group.Key.Status, Count = group.Count() }),
                cancellationToken);

            foreach (var run in page.Items.Where(candidate => live.Contains(candidate.Id)))
            {
                var forRun = counts.Where(entry => entry.RunId == run.Id).ToList();

                int CountOf(CampaignMessageStatus wanted) =>
                    forRun.FirstOrDefault(entry => entry.Status == wanted)?.Count ?? 0;

                run.AudienceSize = forRun.Sum(entry => entry.Count);
                run.SentCount = CountOf(CampaignMessageStatus.Sent)
                                + CountOf(CampaignMessageStatus.Delivered)
                                + CountOf(CampaignMessageStatus.Read);
                run.DeliveredCount = CountOf(CampaignMessageStatus.Delivered) + CountOf(CampaignMessageStatus.Read);
                run.ReadCount = CountOf(CampaignMessageStatus.Read);
                run.FailedCount = CountOf(CampaignMessageStatus.Failed);
            }
        }

        return page.Map(CampaignMapper.ToResponse);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(
        CancellationToken cancellationToken = default)
    {
        // Unpaged, matching the contract: the client filters and paginates in memory today. If a
        // tenant is likely to exceed a couple of hundred campaigns this becomes a PagedResult,
        // which is a breaking change and so belongs before launch rather than after.
        //
        // The whole entity is loaded rather than projected. The response now carries the recurrence
        // rule, the group ids and the run timestamps, and a hand-written projection is one more
        // place for a new field to be forgotten - which reads as a value that is present on the
        // detail screen and missing in the list.
        var rows = await _queries.ToListAsync(
            _campaigns.Query().OrderByDescending(campaign => campaign.CreatedOn),
            cancellationToken);

        return [.. rows.Select(CampaignMapper.ToResponse)];
    }
}
