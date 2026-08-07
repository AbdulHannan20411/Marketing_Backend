using Marketing.Application.Services;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Campaigns;

/// <summary>
/// Advances every campaign that is due to start, resume or continue.
/// <para>
/// Polling rather than a queue. A campaign becomes due for three unrelated reasons - a scheduled
/// time arrives, a rate-limit window frees up, or a previous batch left work behind - and a poll
/// covers all three with one mechanism and no message that can be lost. At this platform's scale
/// the cost is one indexed query a minute.
/// </para>
/// <para>
/// The job holds no dispatch logic of its own: it is a trigger. Everything it does is also
/// reachable from a service call, which is what makes the behaviour testable without a scheduler.
/// </para>
/// </summary>
[ScheduledJob(
    Key = "campaign-dispatch",
    Group = "campaigns",
    // Every minute. A campaign scheduled for 09:00 goes out within a minute of it, which is the
    // resolution the client's scheduler offers; going faster would poll an idle table all night.
    Cron = "0 * * * * ?",
    Description = "Sends the next batch of messages for every campaign that is due.")]
public sealed class CampaignDispatchJob : ScheduledJobBase
{
    private readonly ICampaignDispatchService _dispatcher;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="dispatcher">Dispatch service.</param>
    /// <param name="logger">Logger.</param>
    public CampaignDispatchJob(ICampaignDispatchService dispatcher, ILogger<CampaignDispatchJob> logger)
        : base(logger)
    {
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _dispatcher.DispatchDueCampaignsAsync(cancellationToken);
}
