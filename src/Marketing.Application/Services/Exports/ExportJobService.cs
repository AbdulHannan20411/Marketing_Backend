using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marketing.Application.Contracts;
using Marketing.Application.DTOs.Exports;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Exports;

/// <summary>Creates, lists and serves asynchronous list-view exports.</summary>
public interface IExportJobService
{
    /// <summary>Accepts an export and queues it. Reads no data and writes no file.</summary>
    /// <param name="request">What to export, and the list view's state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ExportAcceptedResponse> CreateAsync(
        CreateExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one of the caller's exports.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ExportJobResponse> GetAsync(string exportId, CancellationToken cancellationToken = default);

    /// <summary>Returns the caller's exports, newest first.</summary>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ExportJobResponse>> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a finished export for download, after checking it is the caller's to have.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ExportDownload> OpenAsync(string exportId, CancellationToken cancellationToken = default);

    /// <summary>Queues a failed export again, with the query it was created with.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ExportAcceptedResponse> RetryAsync(string exportId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws an export that has not finished.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ExportJobResponse> CancelAsync(string exportId, CancellationToken cancellationToken = default);

    /// <summary>Lists what the caller may export, and the columns each list offers.</summary>
    public IReadOnlyList<ExportDatasetResponse> Datasets();
}

/// <inheritdoc cref="IExportJobService" />
public sealed partial class ExportJobService : IExportJobService
{
    /// <summary>How long a completed export can be downloaded before the cleanup job removes it.</summary>
    /// <remarks>
    /// Long enough to survive a weekend, short enough that extracted customer data is not sitting
    /// in storage indefinitely. An export is a copy of the workspace's data outside the
    /// application's own access controls, and the cheapest way to keep that safe is for it not to
    /// exist for long.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// How recently an identical export must have been asked for to count as the same click.
    /// </summary>
    /// <remarks>
    /// Short on purpose. Exporting the same view twice in a morning is a legitimate thing to do -
    /// the data has changed - and refusing it would be the product deciding it knows better. What
    /// this catches is the same request arriving five times because somebody clicked five times,
    /// which is one intention.
    /// </remarks>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

    /// <summary>Largest page the export history will return.</summary>
    private const int MaxPageSize = 50;

    private readonly IRepository<ExportJob> _jobs;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IExportDatasetRegistry _datasets;
    private readonly IEnumerable<IExportFileWriter> _writers;
    private readonly IMessagePublisher _publisher;
    private readonly IFileStorage _storage;
    private readonly ICacheService _cache;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ExportJobService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public ExportJobService(
        IRepository<ExportJob> jobs,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IExportDatasetRegistry datasets,
        IEnumerable<IExportFileWriter> writers,
        IMessagePublisher publisher,
        IFileStorage storage,
        ICacheService cache,
        IDateTimeProvider clock,
        ILogger<ExportJobService> logger)
    {
        _jobs = jobs;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _datasets = datasets;
        _writers = writers;
        _publisher = publisher;
        _storage = storage;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExportAcceptedResponse> CreateAsync(
        CreateExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dataset = Resolve(request.Dataset);
        var userId = Caller();
        var tenantId = _tenantContext.RequireTenantId();

        var format = request.Format ?? ExportFormat.Csv;
        var columns = ResolveColumns(dataset, request.Columns);

        var query = new ExportQuery
        {
            Search = Trimmed(request.Search),
            Filters = request.Filters is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(request.Filters, StringComparer.OrdinalIgnoreCase),
            SortBy = Trimmed(request.SortBy),
            SortDirection = Trimmed(request.SortDirection),
            Columns = [.. columns.Select(column => column.Key)],
        };

        var queryJson = JsonSerializer.Serialize(query, ExportQuery.SerializerOptions);
        var fingerprint = Fingerprint(dataset.Key, format, queryJson);

        if (await AlreadyRunningAsync(userId, fingerprint, cancellationToken) is { } running)
        {
            // Not an error and not a silent drop: the caller is told which export is already
            // doing what they asked for, and the client says so rather than implying a second
            // file is on its way.
            LogDuplicate(running.Id, dataset.Key, userId);

            return new ExportAcceptedResponse(
                PublicId.From(PublicId.ExportJob, running.Id), running.Status, Reused: true);
        }

        var job = new ExportJob
        {
            TenantId = tenantId,
            Dataset = dataset.Key,
            Format = format,
            Status = ExportJobStatus.Queued,
            RequestedByUserId = userId,
            QueryJson = queryJson,
            Columns = [.. columns.Select(column => column.Key)],
            Fingerprint = fingerprint,
        };

        _jobs.Add(job);

        // Saved before the message goes out. The other order publishes an identifier that does
        // not exist yet, and a fast worker would read nothing and fail a job the user can see.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var formatName = format.ToString();

        LogQueued(job.Id, dataset.Key, formatName, tenantId, userId);

        // Fire and forget, deliberately after the commit. If the broker is down the row stays
        // Queued and the sweeper picks it up - which is why the row, not the message, is the
        // record of what has to happen.
        await _publisher.PublishAsync(new ExportJobQueued(job.Id, tenantId), cancellationToken);

        return new ExportAcceptedResponse(
            PublicId.From(PublicId.ExportJob, job.Id), ExportJobStatus.Queued, Reused: false);
    }

    /// <inheritdoc />
    public async Task<ExportJobResponse> GetAsync(string exportId, CancellationToken cancellationToken = default) =>
        Describe(await LoadAsync(exportId, cancellationToken));

    /// <inheritdoc />
    public async Task<PagedResult<ExportJobResponse>> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var userId = Caller();
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var number = Math.Max(page, 1);

        // Scoped to the caller, not to the workspace. An export is one person's extract, and a
        // colleague's presence in the same workspace is not a reason to list theirs.
        var mine = _jobs.Query()
            .Where(job => job.RequestedByUserId == userId)
            .OrderByDescending(job => job.CreatedOn)
            .ThenByDescending(job => job.Id);

        var result = await _queries.ToPagedAsync(mine, number, size, cancellationToken);

        return result.Map(Describe);
    }

    /// <inheritdoc />
    public async Task<ExportDownload> OpenAsync(string exportId, CancellationToken cancellationToken = default)
    {
        var job = await LoadAsync(exportId, cancellationToken);

        // Each of these is a separate reason, and each is a 409 rather than a 404: the caller has
        // already been shown that this export exists, so the useful answer is why they cannot
        // have it yet rather than pretending it is not there.
        if (job.Status == ExportJobStatus.Expired || Expired(job))
        {
            throw new BusinessRuleException(
                "export_expired",
                "That export has expired. Run it again to get a fresh file.");
        }

        if (job.Status != ExportJobStatus.Completed)
        {
            throw new BusinessRuleException(
                "export_not_ready",
                $"That export is {job.Status.ToString().ToLowerInvariant()}. Only a completed export can be downloaded.");
        }

        if (job.FileStorageKey is not { Length: > 0 } key)
        {
            throw new BusinessRuleException("export_not_ready", "That export has no file.");
        }

        // Checked rather than assumed. A file can go missing between the job completing and
        // somebody clicking Download - a cleanup that ran early, a restored backup - and an
        // unhandled storage exception would surface as a 500 on a link the user was invited to
        // click.
        if (!await _storage.ExistsAsync(key, cancellationToken))
        {
            throw new BusinessRuleException(
                "export_expired",
                "That export's file is no longer available. Run it again to get a fresh one.");
        }

        var content = await _storage.OpenAsync(key, cancellationToken);

        LogDownloaded(job.Id, job.Dataset, job.RequestedByUserId);

        return new ExportDownload(content, ContentTypeFor(job.Format), job.FileName ?? FileNameFor(job));
    }

    /// <inheritdoc />
    public async Task<ExportAcceptedResponse> RetryAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadAsync(exportId, tracked: true, cancellationToken);

        // Only a failure is retryable. Requeuing a completed export would write a second file
        // over the first, and requeuing a running one is what the duplicate guard exists to stop.
        if (job.Status != ExportJobStatus.Failed)
        {
            throw new BusinessRuleException(
                "export_not_retryable",
                $"Only a failed export can be retried. That one is {job.Status.ToString().ToLowerInvariant()}.");
        }

        ExportJobStates.MoveTo(job, ExportJobStatus.Queued);

        job.ErrorMessage = null;
        job.ProcessedRecords = 0;
        job.StartedAt = null;
        job.CompletedAt = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var formatName = job.Format.ToString();

        LogQueued(job.Id, job.Dataset, formatName, job.TenantId ?? 0, job.RequestedByUserId);

        await _publisher.PublishAsync(
            new ExportJobQueued(job.Id, job.TenantId ?? _tenantContext.RequireTenantId()), cancellationToken);

        return new ExportAcceptedResponse(
            PublicId.From(PublicId.ExportJob, job.Id), ExportJobStatus.Queued, Reused: false);
    }

    /// <inheritdoc />
    public async Task<ExportJobResponse> CancelAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadAsync(exportId, tracked: true, cancellationToken);

        if (ExportJobStates.IsTerminal(job.Status))
        {
            throw new BusinessRuleException(
                "export_not_cancellable",
                $"That export is already {job.Status.ToString().ToLowerInvariant()}.");
        }

        ExportJobStates.MoveTo(job, ExportJobStatus.Cancelled);

        job.CompletedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Describe(job);
    }

    /// <inheritdoc />
    public IReadOnlyList<ExportDatasetResponse> Datasets() =>
    [
        .. _datasets.All
            // Only what this caller could actually export. Listing a dataset they would be
            // refused is an invitation to a 403.
            .Where(dataset => _currentUser.HasPermission(dataset.Permission))
            .OrderBy(dataset => dataset.DisplayName, StringComparer.Ordinal)
            .Select(dataset => new ExportDatasetResponse(
                dataset.Key,
                dataset.DisplayName,
                [.. dataset.Columns.Select(column =>
                    new ExportColumnResponse(column.Key, column.Heading, column.Default))])),
    ];

    /// <summary>
    /// Finds the dataset, refusing an unknown one and one the caller may not read.
    /// </summary>
    /// <remarks>
    /// In that order. An unknown key is a 404 before the permission is consulted, so a typo
    /// cannot be used to discover which datasets exist by the shape of the refusal.
    /// </remarks>
    /// <param name="key">Registry key from the request.</param>
    private IExportDataset Resolve(string? key)
    {
        var dataset = _datasets.Find(key)
                      ?? throw new NotFoundException("Export dataset", key ?? "(none)");

        if (!_currentUser.HasPermission(dataset.Permission))
        {
            throw new ForbiddenException(
                "forbidden",
                $"You do not have permission to read {dataset.DisplayName.ToLowerInvariant()}, so you cannot export them either.");
        }

        return dataset;
    }

    /// <summary>
    /// Turns the requested column keys into catalogue entries.
    /// </summary>
    /// <remarks>
    /// <b>The allow-list.</b> A key that is not in the dataset's own catalogue is refused rather
    /// than dropped: a client asking for a column that does not exist has misunderstood something,
    /// and silently handing back a file without it is how somebody discovers a missing column
    /// after sending the spreadsheet on. Nothing here is ever a property path.
    /// </remarks>
    /// <param name="dataset">Dataset being exported.</param>
    /// <param name="requested">Column keys from the request, possibly empty.</param>
    private static List<ExportColumn> ResolveColumns(
        IExportDataset dataset,
        IReadOnlyList<string>? requested)
    {
        if (requested is not { Count: > 0 })
        {
            return [.. dataset.Columns.Where(column => column.Default)];
        }

        var catalogue = dataset.Columns.ToDictionary(column => column.Key, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ExportColumn>(requested.Count);

        foreach (var key in requested)
        {
            if (!catalogue.TryGetValue(key, out var column))
            {
                throw new ValidationException(
                    "columns",
                    $"'{key}' is not a column of {dataset.DisplayName}. Allowed: {string.Join(", ", catalogue.Keys)}.");
            }

            // In the order asked for, and without duplicates - a column named twice would be
            // written twice.
            if (!resolved.Contains(column))
            {
                resolved.Add(column);
            }
        }

        if (resolved.Count == 0)
        {
            throw new ValidationException("columns", "An export needs at least one column.");
        }

        return resolved;
    }

    /// <summary>
    /// The same export, already running for this user.
    /// </summary>
    /// <remarks>
    /// Two guards, and the cheaper one is not the reliable one. Redis collapses the burst from a
    /// double click before it reaches the database; the database answers authoritatively and is
    /// what runs when Redis is down, which it is allowed to be. The Redis miss is a miss, never a
    /// refusal - failing a legitimate export because a cache is unreachable would be worse than
    /// running it twice.
    /// </remarks>
    /// <param name="userId">The caller.</param>
    /// <param name="fingerprint">Hash of dataset, format and query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<ExportJob?> AlreadyRunningAsync(
        long userId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var since = _clock.UtcNow - DuplicateWindow;

        return await _queries.FirstOrDefaultAsync(
            _jobs.Query()
                .Where(job => job.RequestedByUserId == userId
                              && job.Fingerprint == fingerprint
                              && (job.Status == ExportJobStatus.Queued || job.Status == ExportJobStatus.Processing)
                              && job.CreatedOn >= since)
                .OrderByDescending(job => job.Id),
            cancellationToken);
    }

    /// <summary>Identifies the same request asked for twice.</summary>
    /// <param name="dataset">Registry key.</param>
    /// <param name="format">Chosen format.</param>
    /// <param name="queryJson">Serialised list-view state.</param>
    private static string Fingerprint(string dataset, ExportFormat format, string queryJson)
    {
        var material = $"{dataset}|{format}|{queryJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Loads one of the caller's exports.
    /// </summary>
    /// <remarks>
    /// Three checks, all of them 404: the identifier must parse with this prefix, the row must be
    /// in the caller's workspace - the repository's tenant filter sees to that - and it must be
    /// the caller's own. Somebody else's export is "not found" rather than "forbidden", because a
    /// 403 would confirm that an identifier belongs to a real export somewhere.
    /// </remarks>
    /// <param name="exportId">Prefixed identifier from the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private Task<ExportJob> LoadAsync(string exportId, CancellationToken cancellationToken) =>
        LoadAsync(exportId, tracked: false, cancellationToken);

    /// <inheritdoc cref="LoadAsync(string, CancellationToken)" />
    /// <param name="exportId">Prefixed identifier from the request.</param>
    /// <param name="tracked">Whether the row is loaded for update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<ExportJob> LoadAsync(string exportId, bool tracked, CancellationToken cancellationToken)
    {
        var userId = Caller();

        if (!PublicId.TryParse(PublicId.ExportJob, exportId, out var id))
        {
            throw new NotFoundException("Export", exportId ?? "(none)");
        }

        var job = await _queries.FirstOrDefaultAsync(
            _jobs.Query(asNoTracking: !tracked).Where(row => row.Id == id),
            cancellationToken);

        if (job is null || job.RequestedByUserId != userId)
        {
            throw new NotFoundException("Export", exportId);
        }

        return job;
    }

    /// <summary>Whether the file's lifetime has run out, whatever the status column says.</summary>
    /// <remarks>
    /// Checked on read as well as by the cleanup job, so an export whose deletion has not run yet
    /// is still refused. A job that expires between two sweeps must not be downloadable in
    /// between.
    /// </remarks>
    /// <param name="job">The export.</param>
    private bool Expired(ExportJob job) =>
        job.ExpiresAt is { } expiresAt && expiresAt <= _clock.UtcNow;

    /// <summary>Projects a job onto the shape every export screen renders.</summary>
    /// <param name="job">The export.</param>
    private ExportJobResponse Describe(ExportJob job)
    {
        var dataset = _datasets.Find(job.Dataset);

        var percentage = job.TotalRecords is > 0
            ? Math.Clamp((int)(job.ProcessedRecords * 100L / job.TotalRecords.Value), 0, 100)

            // Null rather than zero. A total nobody counted means indeterminate, and a bar stuck
            // at 0% for four minutes reads as broken.
            : (int?)null;

        var downloadable = job.Status == ExportJobStatus.Completed
                           && job.FileStorageKey is { Length: > 0 }
                           && !Expired(job);

        return new ExportJobResponse(
            PublicId.From(PublicId.ExportJob, job.Id),
            job.Dataset,
            dataset?.DisplayName ?? job.Dataset,
            job.Format,
            job.Status,
            job.TotalRecords,
            job.ProcessedRecords,
            percentage,
            job.FileName,
            job.FileSizeBytes,
            job.ErrorMessage,
            job.CreatedOn,
            job.CompletedAt,
            job.ExpiresAt,
            downloadable);
    }

    /// <summary>Media type for a format, from the writer that produces it.</summary>
    /// <param name="format">Export format.</param>
    private string ContentTypeFor(ExportFormat format) =>
        _writers.FirstOrDefault(writer => writer.Format == format)?.ContentType
        ?? "application/octet-stream";

    /// <summary>A fallback name, for a job written before one was stored.</summary>
    /// <param name="job">The export.</param>
    private string FileNameFor(ExportJob job) =>
        $"{job.Dataset}-{job.CreatedOn.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
        + (_writers.FirstOrDefault(writer => writer.Format == job.Format)?.FileExtension ?? ".csv");

    private long Caller() =>
        _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "Export {ExportJobId} of {Dataset} as {Format} queued for tenant {TenantId} by user {UserId}.")]
    private partial void LogQueued(long exportJobId, string dataset, string format, long tenantId, long userId);

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Export {ExportJobId} of {Dataset} is already running for user {UserId}; reusing it.")]
    private partial void LogDuplicate(long exportJobId, string dataset, long userId);

    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Information,
        Message = "Export {ExportJobId} of {Dataset} downloaded by user {UserId}.")]
    private partial void LogDownloaded(long exportJobId, string dataset, long userId);
}
