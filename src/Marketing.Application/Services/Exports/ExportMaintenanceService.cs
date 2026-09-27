using Marketing.Application.Contracts;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Exports;

/// <summary>Housekeeping for exports: expiring old files, and rescuing jobs nobody picked up.</summary>
public interface IExportMaintenanceService
{
    /// <summary>
    /// Deletes the files of exports past their lifetime and marks them expired.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many were expired.</returns>
    public Task<int> ExpireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues exports that were never picked up, and ones whose worker died mid-run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many were requeued.</returns>
    public Task<int> RequeueStalledAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IExportMaintenanceService" />
public sealed partial class ExportMaintenanceService : IExportMaintenanceService
{
    /// <summary>
    /// How long a job may sit queued before it is assumed lost and published again.
    /// </summary>
    /// <remarks>
    /// This is what makes the database the record rather than the broker. If RabbitMQ was down
    /// when the export was accepted, the publish went nowhere and the row is the only evidence
    /// anybody asked - so something has to notice. Generous, because a busy worker is not a lost
    /// message.
    /// </remarks>
    private static readonly TimeSpan QueuedGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a job may be processing before its worker is presumed dead.
    /// </summary>
    /// <remarks>
    /// The lease. Long enough for a genuinely large export - a million contacts takes minutes,
    /// not an hour - and short enough that a pod killed mid-export does not leave a job stuck at
    /// 40% until somebody notices.
    /// </remarks>
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromHours(1);

    /// <summary>Rows touched per run, so one sweep cannot become an unbounded transaction.</summary>
    private const int BatchSize = 200;

    private readonly IRepository<ExportJob> _jobs;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _storage;
    private readonly IMessagePublisher _publisher;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ExportMaintenanceService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public ExportMaintenanceService(
        IRepository<ExportJob> jobs,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IFileStorage storage,
        IMessagePublisher publisher,
        IDateTimeProvider clock,
        ILogger<ExportMaintenanceService> logger)
    {
        _jobs = jobs;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _storage = storage;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ExpireAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var due = await _queries.ToListAsync(
            Across()
                .Where(job => job.Status == ExportJobStatus.Completed
                              && job.ExpiresAt != null
                              && job.ExpiresAt <= now)
                .OrderBy(job => job.ExpiresAt)
                .Take(BatchSize),
            cancellationToken);

        foreach (var job in due)
        {
            if (job.FileStorageKey is { Length: > 0 } key)
            {
                try
                {
                    await _storage.DeleteAsync(key, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The row is still marked expired. A file we could not delete is a storage
                    // problem to chase; leaving the job downloadable because of it would be a
                    // retention rule that quietly does not apply.
                    LogFileNotDeleted(exception, job.Id);
                }
            }

            ExportJobStates.MoveTo(job, ExportJobStatus.Expired);

            // Cleared, so nothing can later hand out a key to a file that is gone. The history
            // row survives - what was exported, when, by whom - which is the part worth keeping.
            job.FileStorageKey = null;
        }

        if (due.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            LogExpired(due.Count);
        }

        return due.Count;
    }

    /// <inheritdoc />
    public async Task<int> RequeueStalledAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var queuedBefore = now - QueuedGrace;
        var startedBefore = now - ProcessingLease;

        var stalled = await _queries.ToListAsync(
            Across()
                .Where(job =>
                    (job.Status == ExportJobStatus.Queued && job.CreatedOn <= queuedBefore)
                    || (job.Status == ExportJobStatus.Processing
                        && job.StartedAt != null
                        && job.StartedAt <= startedBefore))
                .OrderBy(job => job.Id)
                .Take(BatchSize),
            cancellationToken);

        foreach (var job in stalled)
        {
            // A processing job whose lease ran out goes back to queued. The worker that had it is
            // gone; if it somehow is not, the claim is optimistic and whichever saves second
            // loses.
            if (job.Status == ExportJobStatus.Processing)
            {
                ExportJobStates.MoveTo(job, ExportJobStatus.Queued);

                job.StartedAt = null;
            }
        }

        if (stalled.Count == 0)
        {
            return 0;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var job in stalled.Where(job => job.TenantId is not null))
        {
            LogRequeued(job.Id, job.Dataset);

            await _publisher.PublishAsync(
                new ExportJobQueued(job.Id, job.TenantId!.Value), cancellationToken);
        }

        return stalled.Count;
    }

    /// <summary>
    /// Every workspace's exports, because maintenance is not anybody's request.
    /// </summary>
    /// <remarks>
    /// The one place in the export code that steps outside the tenant filter, and it reads no
    /// exported data - only the job rows' own bookkeeping. Everything that touches the data
    /// itself runs inside a tenant scope.
    /// </remarks>
    private IQueryable<ExportJob> Across() => _jobs.Query(asNoTracking: false).IgnoreQueryFilters();

    [LoggerMessage(EventId = 4240, Level = LogLevel.Information,
        Message = "Expired {Count} export(s) and removed their files.")]
    private partial void LogExpired(int count);

    [LoggerMessage(EventId = 4241, Level = LogLevel.Warning,
        Message = "Export {ExportJobId} of {Dataset} had stalled; queued again.")]
    private partial void LogRequeued(long exportJobId, string dataset);

    [LoggerMessage(EventId = 4242, Level = LogLevel.Error,
        Message = "Could not delete the file for expired export {ExportJobId}.")]
    private partial void LogFileNotDeleted(Exception exception, long exportJobId);
}
