using Marketing.Application.Configurations;
using Marketing.Business.Repositories.Implementations;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.Services.Imports;

/// <summary>
/// Claims queued import work and runs it.
/// <para>
/// The consumer end of the outbox. It is the only thing that knows work is polled rather than
/// delivered; swapping the database queue for a broker replaces this and the claim query, and
/// nothing else.
/// </para>
/// </summary>
public interface IImportJobRunner
{
    /// <summary>
    /// Claims the next due jobs and runs them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many jobs were run, whether or not they succeeded.</returns>
    public Task<int> RunDueJobsAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IImportJobRunner" />
public sealed partial class ImportJobRunner : IImportJobRunner
{
    private readonly IImportJobRepository _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _clock;
    private readonly ImportOptions _options;
    private readonly ILogger<ImportJobRunner> _logger;

    /// <summary>Initialises a new instance.</summary>
    public ImportJobRunner(
        IImportJobRepository jobs,
        IServiceScopeFactory scopeFactory,
        IUnitOfWork unitOfWork,
        IDateTimeProvider clock,
        IOptions<ImportOptions> options,
        ILogger<ImportJobRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> RunDueJobsAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await _jobs.ClaimDueAsync(
            _clock.UtcNow,
            TimeSpan.FromMinutes(_options.LeaseMinutes),
            _options.PollBatchSize,
            _options.MaxAttempts,
            cancellationToken);

        if (claimed.Count == 0)
        {
            return 0;
        }

        foreach (var job in claimed)
        {
            // Not cancelled between jobs: a claim is already durable, so abandoning the loop here
            // only leaves the job to be reclaimed when its lease expires. Finishing what has been
            // claimed is the cheaper end.
            await RunAsync(job, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return claimed.Count;
    }

    /// <summary>Runs one job and records how it went, whatever happens.</summary>
    private async Task RunAsync(ImportJob job, CancellationToken cancellationToken)
    {
        try
        {
            // Its own container scope, so the work gets a fresh database context under the job's
            // own tenant. Sharing the runner's context would carry one tenant's tracked entities
            // into the next tenant's unit of work.
            await using var scope = _scopeFactory.CreateAsyncScope();

            var tenants = scope.ServiceProvider.GetRequiredService<ITenantContext>();

            if (job.TenantId is not { } tenantId)
            {
                // Nothing can be done with a job that names no tenant: every query it would run is
                // filtered by one. Dead-lettered rather than retried for ever.
                Settle(job, succeeded: false, "The job has no tenant.", exhaust: true);

                return;
            }

            // The worker has no bearer token, so the tenant comes from the job row. This is the
            // only supported way to resolve a tenant outside a request.
            using var tenantScope = tenants.BeginScope(tenantId);

            var processing = scope.ServiceProvider.GetRequiredService<IImportProcessingService>();

            await DispatchAsync(processing, job, cancellationToken);

            Settle(job, succeeded: true, error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A shutdown, not a failure. Released rather than counted against the job, so it is
            // picked up promptly on the next start instead of burning an attempt.
            job.State = ImportJobState.Pending;
            job.ClaimedAt = null;

            throw;
        }
#pragma warning disable CA1031 // A worker must survive any failure in one job.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The message only. An exception from an import can quote the row that caused it, and
            // that row is a contact's name, number and email.
            LogFailed(job.Id, job.Kind.ToString(), exception.GetType().Name);

            Settle(job, succeeded: false, exception.Message);
        }
    }

    /// <summary>Sends a job to the work its kind names.</summary>
    private static Task DispatchAsync(
        IImportProcessingService processing,
        ImportJob job,
        CancellationToken cancellationToken) => job.Kind switch
        {
            ImportJobKind.Process => processing.ProcessAsync(job.ContactImportBatchId, cancellationToken),
            ImportJobKind.Commit => processing.CommitAsync(job.ContactImportBatchId, cancellationToken),
            ImportJobKind.ExportErrors => job.ContactImportExportId is { } exportId
                ? processing.ExportErrorsAsync(exportId, cancellationToken)
                : throw new InvalidOperationException("Export work was queued without an export to fill in."),
            _ => throw new InvalidOperationException($"Unknown import job kind {job.Kind}."),
        };

    /// <summary>Marks a job finished, or schedules a retry with backoff.</summary>
    /// <param name="job">Job to settle.</param>
    /// <param name="succeeded">Whether the attempt worked.</param>
    /// <param name="error">Failure reason, when it did not.</param>
    /// <param name="exhaust">Whether to dead-letter immediately rather than retrying.</param>
    private void Settle(ImportJob job, bool succeeded, string? error, bool exhaust = false)
    {
        ImportJobRepository.Settle(
            job,
            succeeded,
            error,
            _clock.UtcNow,
            exhaust ? 0 : _options.MaxAttempts);
    }

    [LoggerMessage(
        EventId = 2920,
        Level = LogLevel.Error,
        Message = "Import job {JobId} ({Kind}) failed with {ExceptionType}.")]
    private partial void LogFailed(long jobId, string kind, string exceptionType);
}
