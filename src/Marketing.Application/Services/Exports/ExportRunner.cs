using System.Globalization;
using System.Text.Json;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Exports;

/// <summary>Runs one queued export to completion.</summary>
public interface IExportRunner
{
    /// <summary>
    /// Claims an export, writes its file and finishes it.
    /// </summary>
    /// <remarks>
    /// Idempotent. An export that is already running or already finished is left alone and the
    /// call returns, so a redelivered message costs a read rather than a second file and a second
    /// notification.
    /// </remarks>
    /// <param name="exportJobId">Key of the job row.</param>
    /// <param name="tenantId">Workspace the job belongs to, from the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RunAsync(long exportJobId, long tenantId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IExportRunner" />
public sealed partial class ExportRunner : IExportRunner
{
    /// <summary>Attempts before an export is failed for good.</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// Logical folder generated exports are stored under.
    /// </summary>
    /// <remarks>
    /// Its own container, separate from uploads, so a retention rule or a bucket policy can treat
    /// extracted data differently from the files customers gave us.
    /// </remarks>
    private const string StorageContainer = "exports";

    /// <summary>
    /// How long a progress reading survives in the cache.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than the interval between readings, so a client that reconnects mid
    /// export sees the last one rather than nothing, and short enough that a worker killed
    /// halfway does not leave a number claiming to be current an hour later.
    /// </remarks>
    private static readonly TimeSpan ProgressLifetime = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ExportRunner> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="scopes">Scope factory; see the remarks on <see cref="RunAsync"/>.</param>
    /// <param name="logger">Logger.</param>
    public ExportRunner(IServiceScopeFactory scopes, ILogger<ExportRunner> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Claims an export, writes its file and finishes it.
    /// </summary>
    /// <remarks>
    /// <b>Why this creates its own scopes instead of taking services.</b> The read is a single
    /// streamed cursor held open for the whole export, and on Npgsql that connection cannot be
    /// used for anything else while it is open. Progress has to be written to the job row every
    /// thousand rows, which is a second query. So the reads run on one scope and every write runs
    /// on a short-lived one of its own - no two ever touching the connection at the same time,
    /// and no stale tracked copy of the job row surviving between them.
    /// </remarks>
    /// <param name="exportJobId">Key of the job row.</param>
    /// <param name="tenantId">Workspace the job belongs to, from the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunAsync(
        long exportJobId,
        long tenantId,
        CancellationToken cancellationToken = default)
    {
        var claimed = await ClaimAsync(exportJobId, tenantId, cancellationToken);

        if (claimed is null)
        {
            // Already running, already finished, cancelled, or gone. All four are ordinary for a
            // queue that may deliver twice, and none of them is a reason to fail the message.
            return;
        }

        var started = DateTimeOffset.UtcNow;

        try
        {
            var outcome = await WriteAsync(claimed, tenantId, cancellationToken);

            await CompleteAsync(exportJobId, tenantId, outcome, cancellationToken);

            LogCompleted(
                exportJobId,
                claimed.Dataset,
                outcome.RowCount,
                outcome.SizeBytes,
                (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not failure. The row stays Processing and the sweeper requeues it; failing
            // it here would turn a deployment into a screen full of failed exports.
            throw;
        }
        catch (Exception exception)
        {
            // The real exception goes to the log with the job attached. What reaches the user is
            // a sentence, because a stack trace tells them nothing and may say too much.
            LogFailed(exception, exportJobId, claimed.Dataset);

            await FailAsync(exportJobId, tenantId, cancellationToken);
        }
    }

    /// <summary>
    /// Moves a queued export to processing, or reports that it is not ours to run.
    /// </summary>
    /// <remarks>
    /// The idempotency gate, and it is the database that decides. Optimistic concurrency settles
    /// the race between two deliveries arriving together: whichever saves second finds the row
    /// changed underneath it and backs off rather than writing a second file.
    /// </remarks>
    /// <param name="exportJobId">Key of the job row.</param>
    /// <param name="tenantId">Workspace from the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the worker needs to run it, or null when it should not.</returns>
    private async Task<ClaimedExport?> ClaimAsync(
        long exportJobId,
        long tenantId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var tenant = Enter(scope, tenantId);

        var jobs = scope.ServiceProvider.GetRequiredService<IRepository<ExportJob>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var job = await jobs.GetForUpdateAsync(exportJobId, cancellationToken);

        if (job is null)
        {
            LogMissing(exportJobId);

            return null;
        }

        // The message said which workspace; the row is the authority. A mismatch means a message
        // that has been tampered with or a bug, and either way this worker is not going to read
        // one workspace's data under another's scope.
        if (job.TenantId != tenantId)
        {
            LogTenantMismatch(exportJobId, tenantId, job.TenantId ?? 0);

            return null;
        }

        if (job.Status != ExportJobStatus.Queued)
        {
            var status = job.Status.ToString();

            LogNotQueued(exportJobId, status);

            return null;
        }

        if (job.AttemptCount >= MaxAttempts)
        {
            ExportJobStates.MoveTo(job, ExportJobStatus.Failed);

            job.ErrorMessage = "This export failed several times and has been stopped. Try running it again.";
            job.CompletedAt = DateTimeOffset.UtcNow;

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return null;
        }

        ExportJobStates.MoveTo(job, ExportJobStatus.Processing);

        job.AttemptCount++;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.ProcessedRecords = 0;

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another delivery claimed it between the read and the write.
            LogRaceLost(exportJobId);

            return null;
        }

        LogStarted(exportJobId, job.Dataset, job.RequestedByUserId);

        await PushAsync(scope, job, cancellationToken);

        return new ClaimedExport(
            job.Id,
            job.Dataset,
            job.Format,
            job.RequestedByUserId,
            JsonSerializer.Deserialize<ExportQuery>(job.QueryJson, ExportQuery.SerializerOptions) ?? new ExportQuery(),
            job.Columns,
            job.CreatedOn);
    }

    /// <summary>
    /// Reads the rows, writes the file and stores it.
    /// </summary>
    /// <remarks>
    /// Through a temporary file on disk rather than a buffer in memory. A CSV of a million
    /// contacts is a few hundred megabytes; holding it while writing it, and again while
    /// uploading it, is how a worker gets killed by the allocator on the one export that
    /// mattered.
    /// </remarks>
    /// <param name="claimed">What was claimed.</param>
    /// <param name="tenantId">Workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<ExportOutcome> WriteAsync(
        ClaimedExport claimed,
        long tenantId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var tenant = Enter(scope, tenantId);

        var registry = scope.ServiceProvider.GetRequiredService<IExportDatasetRegistry>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var dataset = registry.Find(claimed.Dataset)
                      ?? throw new InvalidOperationException(
                          $"No export dataset is registered under '{claimed.Dataset}'.");

        var writer = scope.ServiceProvider.GetServices<IExportFileWriter>()
                         .FirstOrDefault(candidate => candidate.Format == claimed.Format)
                     ?? throw new InvalidOperationException($"No writer produces {claimed.Format}.");

        var columns = Resolve(dataset, claimed.Columns);

        // Taken before the reader opens, so the count and the stream do not contend for the
        // connection. Null is allowed and means indeterminate progress.
        var total = await dataset.CountAsync(claimed.Query, cancellationToken);

        await SetTotalAsync(claimed, tenantId, total, cancellationToken);

        var temporary = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}{writer.FileExtension}");

        try
        {
            int rows;

            await using (var file = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                rows = await writer.WriteAsync(
                    file,
                    [.. columns.Select(column => column.Heading)],
                    dataset.ReadAsync(claimed.Query, columns, cancellationToken),
                    (written, token) => ReportAsync(claimed, tenantId, written, total, token),
                    cancellationToken);
            }

            var fileName = FileName(dataset.DisplayName, claimed.RequestedAt, writer.FileExtension);

            await using var upload = new FileStream(
                temporary, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var size = upload.Length;
            var key = await storage.SaveAsync(StorageContainer, fileName, upload, cancellationToken);

            return new ExportOutcome(rows, fileName, key, size);
        }
        finally
        {
            // Best effort. A leftover temp file is untidy; throwing here would turn a finished
            // export into a failed one.
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                LogTempNotRemoved(temporary);
            }
            catch (UnauthorizedAccessException)
            {
                LogTempNotRemoved(temporary);
            }
        }
    }

    /// <summary>Records the row count, once, before the read begins.</summary>
    private async Task SetTotalAsync(
        ClaimedExport claimed,
        long tenantId,
        int? total,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var tenant = Enter(scope, tenantId);

        var jobs = scope.ServiceProvider.GetRequiredService<IRepository<ExportJob>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var job = await jobs.GetForUpdateAsync(claimed.JobId, cancellationToken);

        if (job is null)
        {
            return;
        }

        job.TotalRecords = total;

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Publishes progress: to the cache, to the job row and to the user.
    /// </summary>
    /// <remarks>
    /// Called once per batch, never per row - the writer decides the interval. Each call is one
    /// database write and one push, which at a thousand rows a time is affordable and at one row
    /// a time would be the export's dominant cost.
    /// <para>
    /// Failures here are swallowed. Progress is a courtesy; losing a reading must not fail an
    /// export that is otherwise going fine.
    /// </para>
    /// </remarks>
    private async Task ReportAsync(
        ClaimedExport claimed,
        long tenantId,
        int written,
        int? total,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var tenant = Enter(scope, tenantId);

            var jobs = scope.ServiceProvider.GetRequiredService<IRepository<ExportJob>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();

            var job = await jobs.GetForUpdateAsync(claimed.JobId, cancellationToken);

            if (job is null || job.Status != ExportJobStatus.Processing)
            {
                return;
            }

            job.ProcessedRecords = written;

            await unitOfWork.SaveChangesAsync(cancellationToken);

            // The cache carries the reading a reconnecting client asks for without touching the
            // database. It is never the source of truth - the row above is - and it is allowed
            // to be unavailable, in which case this does nothing.
            await cache.SetAsync(
                AppConstants.CacheKeys.ExportProgress(tenantId, job.Id),
                new ExportProgressSnapshot(written, total),
                ProgressLifetime,
                cancellationToken);

            await PushAsync(scope, job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProgressFailed(exception, claimed.JobId);
        }
    }

    /// <summary>Finishes an export and tells the user it is ready.</summary>
    private async Task CompleteAsync(
        long exportJobId,
        long tenantId,
        ExportOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var tenant = Enter(scope, tenantId);

        var jobs = scope.ServiceProvider.GetRequiredService<IRepository<ExportJob>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

        var job = await jobs.GetForUpdateAsync(exportJobId, cancellationToken);

        if (job is null || job.Status != ExportJobStatus.Processing)
        {
            // Cancelled while it ran. The file is already stored; the cleanup job will remove it
            // with the rest, and nothing tells the user an export they withdrew is ready.
            return;
        }

        ExportJobStates.MoveTo(job, ExportJobStatus.Completed);

        job.ProcessedRecords = outcome.RowCount;
        job.TotalRecords ??= outcome.RowCount;
        job.FileName = outcome.FileName;
        job.FileStorageKey = outcome.StorageKey;
        job.FileSizeBytes = outcome.SizeBytes;
        job.CompletedAt = clock.UtcNow;
        job.ExpiresAt = clock.UtcNow.Add(ExportJobService.Lifetime);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await PushAsync(scope, job, cancellationToken);
    }

    /// <summary>Marks an export failed, with a sentence the user can act on.</summary>
    private async Task FailAsync(long exportJobId, long tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var tenant = Enter(scope, tenantId);

            var jobs = scope.ServiceProvider.GetRequiredService<IRepository<ExportJob>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

            var job = await jobs.GetForUpdateAsync(exportJobId, cancellationToken);

            if (job is null || ExportJobStates.IsTerminal(job.Status))
            {
                return;
            }

            ExportJobStates.MoveTo(job, ExportJobStatus.Failed);

            // Deliberately generic. The technical reason is in the log against this job id, and
            // a database message or a stack frame is both useless here and a disclosure.
            job.ErrorMessage = "We could not finish this export. Try running it again.";
            job.CompletedAt = clock.UtcNow;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await PushAsync(scope, job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The export has already failed; failing to record that must not also fail the
            // message and send it round again.
            LogFailureNotRecorded(exception, exportJobId);
        }
    }

    /// <summary>Pushes the job's current state to the one user who asked for it.</summary>
    private static async Task PushAsync(
        AsyncServiceScope scope,
        ExportJob job,
        CancellationToken cancellationToken)
    {
        var realtime = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
        var registry = scope.ServiceProvider.GetRequiredService<IExportDatasetRegistry>();

        var percentage = job.TotalRecords is > 0
            ? Math.Clamp((int)(job.ProcessedRecords * 100L / job.TotalRecords.Value), 0, 100)
            : (int?)null;

        await realtime.PublishExportProgressAsync(
            job.RequestedByUserId,
            new ExportProgress(
                PublicId.From(PublicId.ExportJob, job.Id),
                job.Dataset,
                registry.Find(job.Dataset)?.DisplayName ?? job.Dataset,
                job.Status,
                job.ProcessedRecords,
                job.TotalRecords,
                percentage,
                job.FileName,
                job.ErrorMessage),
            cancellationToken);
    }

    /// <summary>
    /// Enters the job's workspace, so every query the worker makes is filtered to it.
    /// </summary>
    /// <remarks>
    /// The worker has no request and therefore no tenant claim. Without this the global filters
    /// would have no tenant to apply and the export would read across the platform - which is the
    /// single worst thing this code could do.
    /// </remarks>
    private static IDisposable Enter(AsyncServiceScope scope, long tenantId) =>
        scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginScope(tenantId);

    /// <summary>Re-resolves the stored column keys against the dataset's catalogue.</summary>
    /// <remarks>
    /// Checked again here, not trusted from the row. A column could have been retired between the
    /// request and the run, and an unknown key must drop out rather than reach a cell accessor.
    /// </remarks>
    private static List<ExportColumn> Resolve(IExportDataset dataset, IReadOnlyList<string> keys)
    {
        var catalogue = dataset.Columns.ToDictionary(column => column.Key, StringComparer.OrdinalIgnoreCase);

        var resolved = keys
            .Select(key => catalogue.TryGetValue(key, out var column) ? column : null)
            .Where(column => column is not null)
            .Select(column => column!)
            .ToList();

        return resolved.Count > 0 ? resolved : [.. dataset.Columns.Where(column => column.Default)];
    }

    /// <summary>Builds the name the browser saves, from safe parts only.</summary>
    /// <remarks>
    /// Assembled from the dataset's own display name and a date, then sanitised. Nothing the
    /// client sent reaches it - a file name is written to a header and, on some storage backends,
    /// to a path.
    /// </remarks>
    private static string FileName(string displayName, DateTimeOffset requestedAt, string extension)
    {
        var stem = new string([.. displayName.Where(character => char.IsLetterOrDigit(character) || character == ' ')])
            .Trim()
            .Replace(' ', '-')
            .ToLowerInvariant();

        if (stem.Length == 0)
        {
            stem = "export";
        }

        return $"{stem}-{requestedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{extension}";
    }

    /// <summary>What a claim hands the rest of the run.</summary>
    /// <param name="JobId">The job key, carried so the short-lived scopes can find the row again.</param>
    /// <param name="Dataset">Registry key of the list view.</param>
    /// <param name="Format">Chosen file format.</param>
    /// <param name="RequestedByUserId">Who to notify, and whose file this is.</param>
    /// <param name="Query">The captured list-view state.</param>
    /// <param name="Columns">Chosen column keys.</param>
    /// <param name="RequestedAt">When it was asked for; used in the file name.</param>
    private sealed record ClaimedExport(
        long JobId,
        string Dataset,
        ExportFormat Format,
        long RequestedByUserId,
        ExportQuery Query,
        IReadOnlyList<string> Columns,
        DateTimeOffset RequestedAt);

    /// <summary>What writing produced.</summary>
    private sealed record ExportOutcome(int RowCount, string FileName, string StorageKey, long SizeBytes);

    /// <summary>The cached progress reading.</summary>
    /// <param name="Processed">Rows written.</param>
    /// <param name="Total">Rows expected, or null.</param>
    private sealed record ExportProgressSnapshot(int Processed, int? Total);

    [LoggerMessage(EventId = 4210, Level = LogLevel.Information,
        Message = "Export {ExportJobId} of {Dataset} started for user {UserId}.")]
    private partial void LogStarted(long exportJobId, string dataset, long userId);

    [LoggerMessage(EventId = 4211, Level = LogLevel.Information,
        Message = "Export {ExportJobId} of {Dataset} completed: {RowCount} rows, {SizeBytes} bytes, {ElapsedMs} ms.")]
    private partial void LogCompleted(long exportJobId, string dataset, int rowCount, long sizeBytes, long elapsedMs);

    [LoggerMessage(EventId = 4212, Level = LogLevel.Error,
        Message = "Export {ExportJobId} of {Dataset} failed.")]
    private partial void LogFailed(Exception exception, long exportJobId, string dataset);

    [LoggerMessage(EventId = 4213, Level = LogLevel.Warning,
        Message = "Export {ExportJobId} was delivered but no longer exists.")]
    private partial void LogMissing(long exportJobId);

    [LoggerMessage(EventId = 4214, Level = LogLevel.Debug,
        Message = "Export {ExportJobId} was delivered again while {Status}; ignoring.")]
    private partial void LogNotQueued(long exportJobId, string status);

    [LoggerMessage(EventId = 4215, Level = LogLevel.Debug,
        Message = "Export {ExportJobId} was claimed by another worker first.")]
    private partial void LogRaceLost(long exportJobId);

    [LoggerMessage(EventId = 4216, Level = LogLevel.Error,
        Message = "Export {ExportJobId} was delivered for tenant {MessageTenantId} but belongs to {JobTenantId}; refused.")]
    private partial void LogTenantMismatch(long exportJobId, long messageTenantId, long jobTenantId);

    [LoggerMessage(EventId = 4217, Level = LogLevel.Debug,
        Message = "Could not record progress for export {ExportJobId}.")]
    private partial void LogProgressFailed(Exception exception, long exportJobId);

    [LoggerMessage(EventId = 4218, Level = LogLevel.Error,
        Message = "Could not record the failure of export {ExportJobId}.")]
    private partial void LogFailureNotRecorded(Exception exception, long exportJobId);

    [LoggerMessage(EventId = 4219, Level = LogLevel.Debug,
        Message = "Could not remove the temporary export file {Path}.")]
    private partial void LogTempNotRemoved(string path);
}
