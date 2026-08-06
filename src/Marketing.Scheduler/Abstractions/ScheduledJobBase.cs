using System.Diagnostics;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Quartz;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Scheduler.Abstractions;

/// <summary>
/// Base class for every scheduled job.
/// <para>
/// Exists so that a job author writes only the work itself. Timing, structured logging, outcome
/// classification, cancellation handling and the Quartz-specific exception contract are handled
/// once here rather than copy-pasted into each job - which is how, in practice, one job ends up
/// swallowing its own failures.
/// </para>
/// </summary>
public abstract partial class ScheduledJobBase : IJob
{
    private readonly ILogger _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="logger">Logger for the concrete job type.</param>
    protected ScheduledJobBase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Name reported in logs. Defaults to the concrete type name.</summary>
    protected virtual string JobName => GetType().Name;

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        var outcome = JobOutcome.Succeeded;

        LogJobStarted(JobName);

        try
        {
            await ExecuteJobAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Host shutdown, not a failure. Nothing was committed, and the next trigger picks the
            // work back up - so this must not be reported as an error or it will page someone
            // every time the service is deployed.
            outcome = JobOutcome.Cancelled;
            LogJobCancelled(JobName);
        }
        catch (Exception exception)
        {
            outcome = JobOutcome.Failed;

            LogJobFailed(exception, JobName, stopwatch.ElapsedMilliseconds);

            // Wrapping in JobExecutionException hands control to Quartz's misfire and retry policy.
            // An unwrapped exception would escape the scheduler thread and the job would quietly
            // stop being rescheduled.
            throw new JobExecutionException(exception, refireImmediately: false);
        }
        finally
        {
            stopwatch.Stop();

            if (outcome == JobOutcome.Succeeded)
            {
                LogJobCompleted(JobName, stopwatch.ElapsedMilliseconds);
            }
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Job {JobName} started.")]
    private partial void LogJobStarted(string jobName);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Job {JobName} completed in {ElapsedMilliseconds} ms.")]
    private partial void LogJobCompleted(string jobName, long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Information,
        Message = "Job {JobName} was cancelled during shutdown.")]
    private partial void LogJobCancelled(string jobName);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Error,
        Message = "Job {JobName} failed after {ElapsedMilliseconds} ms.")]
    private partial void LogJobFailed(Exception exception, string jobName, long elapsedMilliseconds);

    /// <summary>
    /// Performs the job's work.
    /// <para>
    /// Call business services, not repositories. A job is a trigger, not a place for domain logic -
    /// keeping the logic in a service means the same operation can also be exposed as an endpoint
    /// and unit-tested without a scheduler.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Signalled on host shutdown. Honour it: a job that ignores cancellation delays every
    /// deployment by its own runtime.
    /// </param>
    protected abstract Task ExecuteJobAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Base class for a job that operates inside a single tenant.
/// <para>
/// Enters the tenant scope before the work runs, so the global query filters apply exactly as they
/// would on an HTTP request. Without this a job would execute with no ambient tenant and either
/// see nothing or, worse, need to opt out of the filters to see anything.
/// </para>
/// </summary>
public abstract class TenantScopedJobBase : ScheduledJobBase
{
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="tenantContext">Ambient tenant, entered for the duration of the work.</param>
    /// <param name="logger">Logger for the concrete job type.</param>
    protected TenantScopedJobBase(ITenantContext tenantContext, ILogger logger)
        : base(logger)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>Runs a unit of work scoped to one tenant.</summary>
    /// <param name="tenantId">Tenant to enter.</param>
    /// <param name="work">Work to perform inside the scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected async Task ForTenantAsync(
        Guid tenantId,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        using (_tenantContext.BeginScope(tenantId))
        {
            await work(cancellationToken);
        }
    }
}
