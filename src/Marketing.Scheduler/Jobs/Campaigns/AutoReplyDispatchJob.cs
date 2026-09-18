using Marketing.Application.Services.WhatsApp;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Campaigns;

/// <summary>
/// Answers customers whose automatic reply has come due.
/// </summary>
/// <remarks>
/// Every ten seconds, because the shortest pause a workspace can choose is ten seconds and a reply
/// that arrives a minute after it was promised reads as a broken setting. A pass with nothing waiting
/// costs one indexed query.
/// </remarks>
[ScheduledJob(
    Key = "auto-replies",
    Group = "campaigns",
    Cron = "0/10 * * * * ?",
    Description = "Sends the AI replies that have come due, inside Meta's 24-hour window.")]
public sealed class AutoReplyDispatchJob : ScheduledJobBase
{
    private readonly IAutoReplyDispatchService _replies;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="replies">Dispatcher.</param>
    /// <param name="logger">Logger.</param>
    public AutoReplyDispatchJob(IAutoReplyDispatchService replies, ILogger<AutoReplyDispatchJob> logger)
        : base(logger)
    {
        _replies = replies;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _replies.DispatchDueAsync(cancellationToken);
}
