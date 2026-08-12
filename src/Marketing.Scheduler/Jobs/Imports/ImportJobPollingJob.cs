using Marketing.Application.Services.Imports;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Imports;

/// <summary>
/// Runs whatever import work is waiting.
/// <para>
/// The poller behind the outbox. Work is claimed with <c>FOR UPDATE SKIP LOCKED</c>, so running
/// this on several instances at once is safe and is how the platform scales imports — each poll
/// takes rows no other worker holds.
/// </para>
/// <para>
/// The job holds no import logic of its own: it is a trigger. Everything it does is also reachable
/// from a service call, which is what makes the behaviour testable without a scheduler.
/// </para>
/// </summary>
[ScheduledJob(
    Key = "import-jobs",
    Group = "imports",
    // Every fifteen seconds. An operator watches the wizard while this runs, so the delay between
    // uploading a file and seeing its columns is the interval — anything slower reads as a hang.
    Cron = "0/15 * * * * ?",
    Description = "Claims and runs queued contact-import work.")]
public sealed class ImportJobPollingJob : ScheduledJobBase
{
    private readonly IImportJobRunner _runner;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="runner">Import job runner.</param>
    /// <param name="logger">Logger.</param>
    public ImportJobPollingJob(IImportJobRunner runner, ILogger<ImportJobPollingJob> logger)
        : base(logger)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _runner.RunDueJobsAsync(cancellationToken);
}
