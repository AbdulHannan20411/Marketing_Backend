using System.Linq.Expressions;
using Marketing.Application.DTOs.Imports;
using Marketing.Application.Services;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Imports;

/// <summary>Reads import history for the resolved tenant.</summary>
public interface IImportHistoryService
{
    /// <summary>Returns a filtered, searched page of imports, newest first.</summary>
    /// <param name="query">Paging, search and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ImportBatchListItem>> GetBatchesAsync(
        ImportBatchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one import in full.</summary>
    /// <param name="batchId">Prefixed batch identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotFoundException">
    /// No such import, or it belongs to another tenant. The two are indistinguishable by design.
    /// </exception>
    public Task<ImportBatchDetail> GetBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a page of an import's staged rows.</summary>
    /// <param name="batchId">Prefixed batch identifier.</param>
    /// <param name="filter">Paging and the row status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ImportRowDetail>> GetRowsAsync(
        string batchId,
        ImportRowFilter filter,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IImportHistoryService" />
public sealed class ImportHistoryService : IImportHistoryService
{
    /// <summary>
    /// Sortable columns, matched against the client's <c>sortBy</c>.
    /// <para>
    /// An allow-list, so a client-supplied sort field is matched against known keys and never
    /// reaches the provider as text.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Expression<Func<ContactImportBatch, object?>>> SortableColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fileName"] = batch => batch.FileName,
            ["fileSizeBytes"] = batch => batch.FileSizeBytes,
            ["status"] = batch => batch.Status,
            ["totalRows"] = batch => batch.TotalRows,
            ["failedCount"] = batch => batch.FailedCount,
            ["uploadedAt"] = batch => batch.UploadedOn,
            ["completedAt"] = batch => batch.CommittedOn,
        };

    private readonly IRepository<ContactImportBatch> _batches;
    private readonly IRepository<ContactImportRow> _rows;
    private readonly IRepository<Contact> _contacts;
    private readonly IQueryExecutor _queries;
    private readonly IPlanGuard _planGuard;

    /// <summary>Initialises a new instance.</summary>
    public ImportHistoryService(
        IRepository<ContactImportBatch> batches,
        IRepository<ContactImportRow> rows,
        IRepository<Contact> contacts,
        IQueryExecutor queries,
        IPlanGuard planGuard)
    {
        _batches = batches;
        _rows = rows;
        _contacts = contacts;
        _queries = queries;
        _planGuard = planGuard;
    }

    /// <inheritdoc />
    public async Task<PagedResult<ImportBatchListItem>> GetBatchesAsync(
        ImportBatchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = ApplyFilters(_batches.Query(), query);

        // Newest first by default: an operator opening this screen is almost always looking for
        // the import they just ran, not the first one they ever did.
        var sorted = query.SortBy is { Length: > 0 }
            ? source.ApplySort(query, SortableColumns, batch => batch.UploadedOn)
            : source.OrderByDescending(batch => batch.UploadedOn);

        var page = await _queries.ToPagedAsync(
            sorted.Select(batch => new BatchRow(
                batch.Id,
                batch.FileName,
                batch.FileSizeBytes,
                batch.Status,
                batch.TotalRows,
                batch.ImportedCount,
                batch.UpdatedCount,
                batch.SkippedCount,
                batch.FailedCount,
                batch.DuplicateRows,
                batch.ProcessedRows,
                batch.UploadedByName,
                batch.UploadedOn,
                batch.CommittedOn)),
            query.Page,
            query.PageSize,
            cancellationToken);

        return page.Map(Map);
    }

    /// <inheritdoc />
    public async Task<ImportBatchDetail> GetBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await LoadAsync(batchId, cancellationToken);

        var errorGroups = await _queries.ToListAsync(
            _rows.Query()
                .Where(row => row.ContactImportBatchId == batch.Id && row.ErrorCode != null)
                .GroupBy(row => row.ErrorCode!.Value)
                .Select(group => new ImportErrorGroup(group.Key, group.Count())),
            cancellationToken);

        return new ImportBatchDetail(
            PublicId.From(PublicId.ImportBatch, batch.Id),
            batch.FileName,
            batch.FileSizeBytes,
            ImportMapping.ToContract(batch.Status),
            ImportMapping.Statistics(batch),
            batch.UploadedOn,
            batch.CommittedOn,
            batch.UploadedByName ?? string.Empty,
            HasFailedRecords(batch.FailedCount),
            ImportMapping.ProgressPercent(batch),
            batch.Columns,
            ImportMapping.Suggest(batch.Columns),
            ImportMapping.FromJson(batch.ColumnMapping),
            // Descending, so the reason that stopped the most rows leads the summary.
            [.. errorGroups.OrderByDescending(group => group.Count)],
            batch.FailureReason,
            await PlanLimitAsync(batch, errorGroups, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<PagedResult<ImportRowDetail>> GetRowsAsync(
        string batchId,
        ImportRowFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var batch = await LoadAsync(batchId, cancellationToken);

        var source = _rows.Query().Where(row => row.ContactImportBatchId == batch.Id);

        if (!string.IsNullOrWhiteSpace(filter.Status)
            && !string.Equals(filter.Status, ImportRowFilter.All, StringComparison.OrdinalIgnoreCase))
        {
            // An unrecognised status matches nothing rather than everything. Silently ignoring it
            // would show an operator the whole file and let them believe it was filtered.
            var status = Enum.TryParse<RowStatus>(filter.Status, ignoreCase: true, out var parsed)
                ? parsed
                : (RowStatus?)null;

            source = status is { } value
                ? source.Where(row => row.Status == value)
                : source.Where(_ => false);
        }

        // A page of rows, never the whole file. Fifty thousand rows is not a payload.
        var page = await _queries.ToPagedAsync(
            source
                .OrderBy(row => row.RowNumber)
                .Select(row => new RowProjection(
                    row.RowNumber,
                    row.Status,
                    row.Values,
                    row.ErrorCode,
                    row.ErrorField,
                    row.Error)),
            filter.Page,
            filter.PageSize,
            cancellationToken);

        return page.Map(row => new ImportRowDetail(
            row.RowNumber,
            row.Status,
            // Keyed against the batch's headers so the client can render one table column per
            // detected column without tracking positions.
            ImportMapping.Keyed(batch.Columns, row.Values),
            row.ErrorCode is { } code
                ? [new ImportRowErrorDetail(code, row.ErrorField, row.Error ?? ImportMapping.Describe(code))]
                : []));
    }

    /// <summary>Applies search and every list filter, treating <c>all</c> as no filter.</summary>
    private static IQueryable<ContactImportBatch> ApplyFilters(
        IQueryable<ContactImportBatch> source,
        ImportBatchQuery query)
    {
        source = source.WhereFileNameMatches(query.Search);

        if (!string.IsNullOrWhiteSpace(query.Status)
            && !string.Equals(query.Status, ImportBatchQuery.All, StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<BatchStatus>(query.Status, ignoreCase: true, out var status))
        {
            // Expanded to every stored status that presents as the requested one, because the
            // contract's statuses are a projection of the domain's rather than a copy.
            var stored = Enum.GetValues<ContactImportStatus>()
                .Where(candidate => ImportMapping.ToContract(candidate) == status)
                .ToArray();

            source = source.Where(batch => stored.Contains(batch.Status));
        }

        if (query.From is { } from)
        {
            source = source.Where(batch => batch.UploadedOn >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(batch => batch.UploadedOn <= to);
        }

        return source;
    }

    /// <summary>Loads a batch by public identifier, or reports it missing.</summary>
    private async Task<ContactImportBatch> LoadAsync(string batchId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.ImportBatch, batchId, "import");

        return await _queries.FirstOrDefaultAsync(
            _batches.Query().Where(candidate => candidate.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Import", batchId);
    }

    /// <summary>
    /// The plan ceiling, reported only when it bears on this import.
    /// </summary>
    /// <remarks>
    /// Omitted entirely on an unlimited plan. Showing "0 of unlimited" invites the operator to
    /// reason about a limit that does not exist.
    /// </remarks>
    private async Task<ImportPlanLimit?> PlanLimitAsync(
        ContactImportBatch batch,
        IReadOnlyList<ImportErrorGroup> errorGroups,
        CancellationToken cancellationToken)
    {
        var remaining = await _planGuard.RemainingContactCapacityAsync(cancellationToken);

        if (remaining == int.MaxValue)
        {
            return null;
        }

        var current = await _contacts.CountAsync(cancellationToken: cancellationToken);

        var skipped = errorGroups
            .FirstOrDefault(group => group.Code == ImportErrorCode.PlanLimitExceeded)?.Count ?? 0;

        return new ImportPlanLimit(current + remaining, current, skipped);
    }

    /// <summary>Whether a failed-record export is worth offering.</summary>
    private static bool HasFailedRecords(int failedCount) => failedCount > 0;

    private static ImportBatchListItem Map(BatchRow row) =>
        new(
            PublicId.From(PublicId.ImportBatch, row.Id),
            row.FileName,
            row.FileSizeBytes,
            ImportMapping.ToContract(row.Status),
            new ImportStatistics(
                row.TotalRows,
                row.ImportedCount,
                row.UpdatedCount,
                row.DuplicateRows,
                row.FailedCount,
                row.SkippedCount),
            row.UploadedOn,
            row.CommittedOn,
            row.UploadedByName ?? string.Empty,
            HasFailedRecords(row.FailedCount));

    /// <summary>Database-shaped projection, before identifiers are formatted for the wire.</summary>
    private sealed record BatchRow(
        long Id,
        string FileName,
        long FileSizeBytes,
        ContactImportStatus Status,
        int TotalRows,
        int ImportedCount,
        int UpdatedCount,
        int SkippedCount,
        int FailedCount,
        int DuplicateRows,
        int ProcessedRows,
        string? UploadedByName,
        DateTimeOffset UploadedOn,
        DateTimeOffset? CommittedOn);

    /// <summary>Database-shaped row projection.</summary>
    private sealed record RowProjection(
        int RowNumber,
        RowStatus Status,
        List<string> Values,
        ImportErrorCode? ErrorCode,
        string? ErrorField,
        string? Error);
}
