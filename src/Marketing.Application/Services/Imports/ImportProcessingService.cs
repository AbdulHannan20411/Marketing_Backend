using System.Runtime.CompilerServices;
using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.DTOs.Imports;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Imports;

/// <summary>
/// The work the import workers actually do.
/// <para>
/// Separate from the job runner so each unit is reachable from a plain service call. A worker that
/// can only be exercised through a scheduler is a worker that is never tested.
/// </para>
/// <para>
/// Every method is idempotent by way of the state machine: a redelivered or retried job finds the
/// batch past the status the work is valid from and does nothing, rather than importing the same
/// file twice.
/// </para>
/// </summary>
public interface IImportProcessingService
{
    /// <summary>Reads an uploaded file, validates it and stages its rows.</summary>
    /// <param name="batchId">Internal batch key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ProcessAsync(long batchId, CancellationToken cancellationToken = default);

    /// <summary>Writes a batch's staged rows as contacts.</summary>
    /// <param name="batchId">Internal batch key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task CommitAsync(long batchId, CancellationToken cancellationToken = default);

    /// <summary>Builds the workbook of rows a batch could not use.</summary>
    /// <param name="exportId">Internal export key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ExportErrorsAsync(long exportId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IImportProcessingService" />
public sealed partial class ImportProcessingService : IImportProcessingService
{
    /// <summary>Container generated reports are stored under.</summary>
    private const string ReportContainer = "contact-import-reports";

    /// <summary>Rows staged per save while parsing, to bound the change tracker.</summary>
    private const int StageChunkSize = 1_000;

    private readonly IRepository<ContactImportBatch> _batches;
    private readonly IRepository<ContactImportRow> _rows;
    private readonly IRepository<ContactImportExport> _exports;
    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<ContactTag> _tags;
    private readonly IRepository<ContactGroup> _groups;
    private readonly IRepository<ContactTagAssignment> _tagAssignments;
    private readonly IRepository<ContactGroupMember> _groupMembers;
    private readonly IImportFileReaderFactory _readers;
    private readonly IImportErrorReportWriter _reportWriter;
    private readonly IFileStorage _storage;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPlanGuard _planGuard;
    private readonly IRealtimeNotifier _realtime;
    private readonly IDateTimeProvider _clock;
    private readonly ImportOptions _options;
    private readonly ILogger<ImportProcessingService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public ImportProcessingService(
        IRepository<ContactImportBatch> batches,
        IRepository<ContactImportRow> rows,
        IRepository<ContactImportExport> exports,
        IRepository<Contact> contacts,
        IRepository<ContactTag> tags,
        IRepository<ContactGroup> groups,
        IRepository<ContactTagAssignment> tagAssignments,
        IRepository<ContactGroupMember> groupMembers,
        IImportFileReaderFactory readers,
        IImportErrorReportWriter reportWriter,
        IFileStorage storage,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IPlanGuard planGuard,
        IRealtimeNotifier realtime,
        IDateTimeProvider clock,
        IOptions<ImportOptions> options,
        ILogger<ImportProcessingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _batches = batches;
        _rows = rows;
        _exports = exports;
        _contacts = contacts;
        _tags = tags;
        _groups = groups;
        _tagAssignments = tagAssignments;
        _groupMembers = groupMembers;
        _readers = readers;
        _reportWriter = reportWriter;
        _storage = storage;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _planGuard = planGuard;
        _realtime = realtime;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ProcessAsync(long batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _batches.GetForUpdateAsync(batchId, cancellationToken);

        if (batch is null || !ImportStateMachine.ShouldProcess(batch, ContactImportStatus.Queued))
        {
            // Not an error. A retry, a redelivery, or a batch the operator cancelled between the
            // job being queued and this worker claiming it.
            LogBatchSkipped(batchId, batch?.Status);

            return;
        }

        ImportStateMachine.Transition(batch, ContactImportStatus.Processing);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await PublishAsync(batch, cancellationToken);

        try
        {
            await ReadAsync(batch, cancellationToken);
        }
        catch (ValidationException exception)
        {
            // The file itself is unusable — unreadable, empty, too many rows. There is no retry
            // that helps, so the batch fails now with the sentence the operator needs.
            await FailAsync(batch, exception.Message, cancellationToken);

            return;
        }

        ImportStateMachine.Transition(batch, ContactImportStatus.AwaitingMapping);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await PublishAsync(batch, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CommitAsync(long batchId, CancellationToken cancellationToken = default)
    {
        var batch = await _batches.GetForUpdateAsync(batchId, cancellationToken);

        if (batch is null || !ImportStateMachine.ShouldProcess(batch, ContactImportStatus.Committing))
        {
            LogBatchSkipped(batchId, batch?.Status);

            return;
        }

        var map = ImportMapping.FromJson(batch.ColumnMapping)
                  ?? throw new BusinessRuleException(
                      "import_not_mapped",
                      "The import reached the commit stage with no column mapping.");

        var indexes = ColumnIndexes.Resolve(batch.Columns, map);

        // Fetched once and extended as the file introduces new names. A lookup per row would be
        // fifty thousand round trips for a file that usually names three tags in total.
        var tagsByName = await LoadByNameAsync(_tags.Query(asNoTracking: false), tag => tag.Name, cancellationToken);
        var groupsByName = await LoadByNameAsync(
            _groups.Query(asNoTracking: false), group => group.Name, cancellationToken);

        // The ceiling stops the import; it does not fail it. An operator who has waited for a
        // 40,000-row upload should get the 12,000 that fit, not an error and nothing.
        var capacity = await _planGuard.RemainingContactCapacityAsync(cancellationToken);

        var lastRowNumber = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Paged by row number rather than by offset, and only over rows still waiting. A
            // retried commit resumes where the last attempt stopped instead of redoing its work.
            var chunk = await _queries.ToListAsync(
                _rows.Query(asNoTracking: false)
                    .Where(row => row.ContactImportBatchId == batch.Id
                                  && row.RowNumber > lastRowNumber
                                  && (row.Status == RowStatus.Pending
                                      || row.Status == RowStatus.Valid

                                      // Duplicate is what the parse observed, not what the commit
                                      // decided. Excluding it here would mean an import set to
                                      // update existing contacts never updated one.
                                      || row.Status == RowStatus.Duplicate))
                    .OrderBy(row => row.RowNumber)
                    .Take(_options.CommitChunkSize),
                cancellationToken);

            if (chunk.Count == 0)
            {
                break;
            }

            lastRowNumber = chunk[^1].RowNumber;

            capacity = await WriteChunkAsync(
                batch, chunk, indexes, tagsByName, groupsByName, capacity, cancellationToken);

            batch.ProcessedRows += chunk.Count;

            // One transaction per chunk. A single transaction over the whole file would hold locks
            // for minutes and discard every row because of the last one; this bounds both, and the
            // progress counter tells the operator exactly how far it got.
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await PublishAsync(batch, cancellationToken);
        }

        await FinishAsync(batch, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExportErrorsAsync(long exportId, CancellationToken cancellationToken = default)
    {
        var export = await _exports.GetForUpdateAsync(exportId, cancellationToken);

        if (export is null || export.Status != ExportStatus.Pending)
        {
            LogExportSkipped(exportId, export?.Status);

            return;
        }

        var batch = await _batches.GetByIdAsync(export.ContactImportBatchId, cancellationToken)
                    ?? throw new NotFoundException("Import", export.ContactImportBatchId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));

        export.Status = ExportStatus.Processing;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Built in memory, then stored in one write. The row cap on an upload is what bounds this;
        // a spreadsheet library cannot stream a workbook out in any case.
        await using var buffer = new MemoryStream();

        var written = await _reportWriter.WriteAsync(
            buffer, batch.Columns, FailedRowsAsync(batch.Id, cancellationToken), cancellationToken);

        buffer.Position = 0;

        export.StorageKey = await _storage.SaveAsync(
            ReportContainer, export.FileName, buffer, cancellationToken);
        export.RowCount = written;
        export.Status = ExportStatus.Completed;
        export.CompletedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the file, stages every row and records what the parse observed.
    /// </summary>
    /// <remarks>
    /// Validation happens here rather than at commit time so the operator sees the damage before
    /// they commit to it, and so the commit is a write rather than a second full pass.
    /// </remarks>
    private async Task ReadAsync(ContactImportBatch batch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(batch.StorageKey))
        {
            throw new ValidationException("file", "The uploaded file is no longer available.");
        }

        var reader = _readers.For(batch.FileName);

        List<string> columns;

        await using (var header = await _storage.OpenAsync(batch.StorageKey, cancellationToken))
        {
            columns = [.. (await reader.ReadHeaderAsync(header, cancellationToken))
                .Select(column => column.Trim())];
        }

        if (columns.Count == 0 || columns.All(string.IsNullOrWhiteSpace))
        {
            throw new ValidationException("file", "The file has no header row.");
        }

        batch.Columns = columns;

        var map = ImportMapping.Suggest(columns);
        var phoneIndex = IndexOf(columns, map.PhoneNumber);
        var emailIndex = IndexOf(columns, map.Email);

        // Every stored number is pulled once and matched in memory, for the same reason the tags
        // are: a query per row is fifty thousand round trips.
        var existing = new HashSet<string>(
            await _queries.ToListAsync(
                _contacts.Query().Select(contact => contact.NormalizedPhoneNumber), cancellationToken),
            StringComparer.Ordinal);

        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var counters = new ParseCounters();
        var staged = 0;

        await using var content = await _storage.OpenAsync(batch.StorageKey, cancellationToken);

        await foreach (var values in reader.ReadRowsAsync(content, cancellationToken))
        {
            counters.Total++;

            if (counters.Total > _options.MaxRows)
            {
                throw new ValidationException(
                    "file",
                    $"The file has more than {_options.MaxRows:N0} rows. Split it into smaller files.");
            }

            // +1 for the header, +1 because spreadsheet rows are one-based.
            var rowNumber = counters.Total + 1;

            _rows.Add(Stage(batch, rowNumber, values, phoneIndex, emailIndex, existing, seenInFile, counters));

            if (++staged >= StageChunkSize)
            {
                // Saved in chunks so the change tracker never holds the whole file. Left outside a
                // transaction on purpose: a batch that dies mid-parse is failed and re-uploaded,
                // and the partial rows go with it.
                batch.ProcessedRows = counters.Total;
                batch.TotalRows = counters.Total;

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await PublishAsync(batch, cancellationToken);

                staged = 0;
            }
        }

        if (counters.Total == 0)
        {
            throw new ValidationException("file", "The file has a header row but no data rows.");
        }

        batch.TotalRows = counters.Total;
        batch.ProcessedRows = counters.Total;
        batch.DuplicateRows = counters.DuplicatesExisting + counters.DuplicatesInFile;
        batch.DuplicatesInFile = counters.DuplicatesInFile;
        batch.InvalidRows = counters.Invalid;
        batch.FailedCount = counters.Invalid;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Validates one row and returns it staged, ready to insert.</summary>
    private static ContactImportRow Stage(
        ContactImportBatch batch,
        int rowNumber,
        IReadOnlyList<string> values,
        int phoneIndex,
        int emailIndex,
        HashSet<string> existing,
        HashSet<string> seenInFile,
        ParseCounters counters)
    {
        var raw = Cell(values, phoneIndex);
        var normalized = PhoneNumbers.Normalise(raw);

        var row = new ContactImportRow
        {
            TenantId = batch.TenantId,

            // By navigation: on the first chunk the batch may still be unsaved, and assigning the
            // foreign key would store a zero.
            ContactImportBatch = batch,
            RowNumber = rowNumber,
            Values = [.. values],
            Status = RowStatus.Valid,
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            counters.Invalid++;

            return Fail(row, ImportErrorCode.MissingRequiredField, nameof(Contact.PhoneNumber));
        }

        if (!PhoneNumbers.IsPlausible(raw))
        {
            counters.Invalid++;

            return Fail(row, ImportErrorCode.InvalidPhoneNumber, nameof(Contact.PhoneNumber));
        }

        // An unusable email does not fail the row: the number is what makes a contact reachable,
        // and refusing an otherwise good record over a typo in a field nobody messages is not a
        // trade the operator would make. It is dropped at commit time instead.
        if (Cell(values, emailIndex) is { Length: > 0 } email && !MailIsUsable(email))
        {
            row.Error = ImportMapping.Describe(ImportErrorCode.InvalidEmail);
            row.ErrorCode = ImportErrorCode.InvalidEmail;
            row.ErrorField = nameof(Contact.Email);
        }

        if (existing.Contains(normalized))
        {
            counters.DuplicatesExisting++;

            row.IsDuplicate = true;
            row.Status = RowStatus.Duplicate;
            row.ErrorCode = ImportErrorCode.DuplicateContact;
            row.ErrorField = nameof(Contact.PhoneNumber);
            row.Error = ImportMapping.Describe(ImportErrorCode.DuplicateContact);
        }
        else if (!seenInFile.Add(normalized))
        {
            // Counted apart from a collision with a stored contact: the operator resolves the two
            // differently, and the wizard shows both.
            counters.DuplicatesInFile++;

            row.IsDuplicate = true;
            row.Status = RowStatus.Duplicate;
            row.ErrorCode = ImportErrorCode.DuplicateInFile;
            row.ErrorField = nameof(Contact.PhoneNumber);
            row.Error = ImportMapping.Describe(ImportErrorCode.DuplicateInFile);
        }

        return row;

        static ContactImportRow Fail(ContactImportRow row, ImportErrorCode code, string field)
        {
            row.Status = RowStatus.Failed;
            row.ErrorCode = code;
            row.ErrorField = field;
            row.Error = ImportMapping.Describe(code);

            return row;
        }
    }

    /// <summary>Writes one chunk of rows as contacts and returns the capacity that remains.</summary>
    private async Task<int> WriteChunkAsync(
        ContactImportBatch batch,
        IReadOnlyList<ContactImportRow> chunk,
        ColumnIndexes indexes,
        Dictionary<string, ContactTag> tagsByName,
        Dictionary<string, ContactGroup> groupsByName,
        int capacity,
        CancellationToken cancellationToken)
    {
        var numbers = chunk
            .Select(row => PhoneNumbers.Normalise(Cell(row.Values, indexes.Phone)))
            .Where(number => number.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Only the numbers this chunk touches, rather than the tenant's whole audience. A million
        // stored contacts must not become a million tracked entities to import five hundred rows.
        var stored = (await _queries.ToListAsync(
                _contacts.Query(asNoTracking: false)
                    .Where(contact => numbers.Contains(contact.NormalizedPhoneNumber)),
                cancellationToken))
            .ToDictionary(contact => contact.NormalizedPhoneNumber, StringComparer.Ordinal);

        foreach (var row in chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var raw = Cell(row.Values, indexes.Phone);
            var normalized = PhoneNumbers.Normalise(raw);

            if (!PhoneNumbers.IsPlausible(raw))
            {
                // Re-checked rather than trusted, because the mapping may name a different column
                // than the one the parse guessed at.
                Fail(row, ImportErrorCode.InvalidPhoneNumber, nameof(Contact.PhoneNumber));

                continue;
            }

            if (stored.TryGetValue(normalized, out var existing))
            {
                if (batch.DuplicateStrategy != ImportDuplicateStrategy.Update)
                {
                    row.Status = RowStatus.Skipped;

                    continue;
                }

                Apply(existing, row, indexes, _clock.UtcNow);
                AssignNamed(existing, batch.TenantId, row, indexes, tagsByName, groupsByName);

                row.Status = RowStatus.Updated;

                continue;
            }

            if (capacity <= 0)
            {
                Fail(row, ImportErrorCode.PlanLimitExceeded, null);
                row.Status = RowStatus.Skipped;

                continue;
            }

            var contact = new Contact
            {
                TenantId = batch.TenantId,
                FullName = string.Empty,
                PhoneNumber = PhoneNumbers.ToDisplayForm(raw),
                NormalizedPhoneNumber = normalized,
                Status = ContactStatus.Subscribed,

                // Imported contacts carry no opt-in timestamp unless the file gives a status the
                // operator vouches for: the platform has no evidence of when consent was given, and
                // inventing one would fabricate a compliance record.
                OptedInAt = null,
            };

            Apply(contact, row, indexes, _clock.UtcNow);

            _contacts.Add(contact);
            AssignNamed(contact, batch.TenantId, row, indexes, tagsByName, groupsByName);

            stored[normalized] = contact;
            capacity--;

            row.Status = RowStatus.Imported;
        }

        return capacity;

        static void Fail(ContactImportRow row, ImportErrorCode code, string? field)
        {
            row.Status = RowStatus.Failed;
            row.ErrorCode = code;
            row.ErrorField = field;
            row.Error = ImportMapping.Describe(code);
        }
    }

    /// <summary>Copies a row's mapped cells onto a contact.</summary>
    private static void Apply(Contact contact, ContactImportRow row, ColumnIndexes indexes, DateTimeOffset utcNow)
    {
        var raw = Cell(row.Values, indexes.Phone) ?? contact.PhoneNumber;

        if (Cell(row.Values, indexes.Name) is { Length: > 0 } name)
        {
            contact.FullName = name.Trim();
        }
        else if (string.IsNullOrEmpty(contact.FullName))
        {
            // A contact with no name still has to render in a list, and the number is the only
            // thing guaranteed to be there.
            contact.FullName = PhoneNumbers.ToDisplayForm(raw);
        }

        if (Cell(row.Values, indexes.Email) is { Length: > 0 } email && MailIsUsable(email))
        {
            contact.Email = email.Trim();
        }

        // An unrecognised country is left as it was rather than refused. The row is otherwise
        // usable, and failing an import over a spelling is not a trade the operator would make.
        contact.Country = Countries.ToStorageCode(Cell(row.Values, indexes.Country))
                          ?? Countries.FromPhoneNumber(raw)
                          ?? contact.Country;

        if (Enum.TryParse<ContactStatus>(Cell(row.Values, indexes.Status), ignoreCase: true, out var status))
        {
            contact.Status = status;
        }

        // The stamp records when this platform started treating them as subscribed — not a claim
        // about when consent was originally given, which an imported file cannot evidence.
        contact.OptedInAt = ContactRules.ConsentStampFor(contact.Status, utcNow, contact.OptedInAt);
    }

    /// <summary>Creates the tags and groups a row names, then assigns them.</summary>
    private void AssignNamed(
        Contact contact,
        long? tenantId,
        ContactImportRow row,
        ColumnIndexes indexes,
        Dictionary<string, ContactTag> tagsByName,
        Dictionary<string, ContactGroup> groupsByName)
    {
        foreach (var name in SplitList(Cell(row.Values, indexes.Tags)))
        {
            if (!tagsByName.TryGetValue(name, out var tag))
            {
                tag = new ContactTag { TenantId = tenantId, Name = name };

                _tags.Add(tag);
                tagsByName[name] = tag;
            }

            _tagAssignments.Add(new ContactTagAssignment
            {
                TenantId = tenantId,
                Contact = contact,
                ContactTag = tag,
            });
        }

        foreach (var name in SplitList(Cell(row.Values, indexes.Groups)))
        {
            if (!groupsByName.TryGetValue(name, out var group))
            {
                group = new ContactGroup { TenantId = tenantId, Name = name };

                _groups.Add(group);
                groupsByName[name] = group;
            }

            _groupMembers.Add(new ContactGroupMember
            {
                TenantId = tenantId,
                Contact = contact,
                ContactGroup = group,
            });
        }
    }

    /// <summary>Recomputes the batch's counters from its rows and closes it out.</summary>
    private async Task FinishAsync(ContactImportBatch batch, CancellationToken cancellationToken)
    {
        // Counted from the rows rather than accumulated as we went, so a resumed commit reports the
        // truth about the whole file instead of the truth about its final attempt.
        var counts = await _queries.ToListAsync(
            _rows.Query()
                .Where(row => row.ContactImportBatchId == batch.Id)
                .GroupBy(row => row.Status)
                .Select(group => new StatusCount(group.Key, group.Count())),
            cancellationToken);

        int Count(RowStatus status) =>
            counts.FirstOrDefault(entry => entry.Status == status)?.Count ?? 0;

        batch.ImportedCount = Count(RowStatus.Imported);
        batch.UpdatedCount = Count(RowStatus.Updated);
        batch.SkippedCount = Count(RowStatus.Skipped) + Count(RowStatus.Duplicate);
        batch.FailedCount = Count(RowStatus.Failed);
        batch.CommittedOn = _clock.UtcNow;

        ImportStateMachine.Transition(
            batch,
            batch.FailedCount > 0 ? ContactImportStatus.CompletedWithErrors : ContactImportStatus.Completed);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await PublishAsync(batch, cancellationToken);
    }

    /// <summary>Fails a batch with a reason the operator can act on.</summary>
    private async Task FailAsync(ContactImportBatch batch, string reason, CancellationToken cancellationToken)
    {
        ImportStateMachine.Transition(batch, ContactImportStatus.Failed);

        batch.FailureReason = reason;
        batch.CommittedOn = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await PublishAsync(batch, cancellationToken);
    }

    /// <summary>Streams a batch's failed rows for the report.</summary>
    private async IAsyncEnumerable<ImportFailedRow> FailedRowsAsync(
        long batchId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = await _queries.ToListAsync(
            _rows.Query()
                .Where(row => row.ContactImportBatchId == batchId && row.Status == RowStatus.Failed)
                .OrderBy(row => row.RowNumber)
                .Select(row => new FailedProjection(row.RowNumber, row.Values, row.ErrorCode, row.Error)),
            cancellationToken);

        foreach (var row in rows)
        {
            yield return new ImportFailedRow(
                row.RowNumber,
                row.Values,
                row.ErrorCode?.ToString() ?? string.Empty,
                row.Error ?? (row.ErrorCode is { } code ? ImportMapping.Describe(code) : string.Empty));
        }
    }

    /// <summary>Pushes the batch's current state to whoever is watching the wizard.</summary>
    private async Task PublishAsync(ContactImportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.TenantId is not { } tenantId)
        {
            return;
        }

        await _realtime.PublishImportProgressAsync(
            tenantId,
            new ImportProgress(
                PublicId.From(PublicId.ImportBatch, batch.Id),
                ImportMapping.ToContract(batch.Status),
                ImportMapping.ProgressPercent(batch),
                ImportMapping.Statistics(batch),
                batch.FailureReason),
            cancellationToken);
    }

    /// <summary>Loads an entity set keyed by name, tracked, for the commit to extend.</summary>
    private async Task<Dictionary<string, TEntity>> LoadByNameAsync<TEntity>(
        IQueryable<TEntity> source,
        Func<TEntity, string> name,
        CancellationToken cancellationToken)
        where TEntity : BaseEntity
    {
        var rows = await _queries.ToListAsync(source, cancellationToken);

        // Case-insensitive, matching the partial unique index: a file naming "VIP" must find the
        // stored "vip" rather than creating a second tag the operator then has to merge.
        return rows.ToDictionary(name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitList(string? cell) =>
        string.IsNullOrWhiteSpace(cell)
            ? []
            : cell.Split(ImportDelimiters.List, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool MailIsUsable(string email) => System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _);

    private static int IndexOf(List<string> columns, string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return -1;
        }

        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], columnName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? Cell(IReadOnlyList<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : null;

    [LoggerMessage(
        EventId = 2910,
        Level = LogLevel.Information,
        Message = "Import work for batch {BatchId} was skipped; it is {Status}.")]
    private partial void LogBatchSkipped(long batchId, ContactImportStatus? status);

    [LoggerMessage(
        EventId = 2911,
        Level = LogLevel.Information,
        Message = "Export work for {ExportId} was skipped; it is {Status}.")]
    private partial void LogExportSkipped(long exportId, ExportStatus? status);

    /// <summary>Where each mapped field sits in the file's columns.</summary>
    private sealed record ColumnIndexes(int Phone, int Name, int Email, int Country, int Status, int Tags, int Groups)
    {
        /// <summary>Resolves a saved mapping against the file's headers.</summary>
        /// <exception cref="BusinessRuleException">The mapping does not name a usable number column.</exception>
        public static ColumnIndexes Resolve(List<string> columns, ImportColumnMap map)
        {
            var phone = IndexOf(columns, map.PhoneNumber);

            if (phone < 0)
            {
                throw new BusinessRuleException(
                    "import_not_mapped",
                    "The saved mapping does not name a column that holds the phone number.");
            }

            return new ColumnIndexes(
                phone,
                IndexOf(columns, map.FullName),
                IndexOf(columns, map.Email),
                IndexOf(columns, map.Country),
                IndexOf(columns, map.Status),
                IndexOf(columns, map.Tags),
                IndexOf(columns, map.Groups));
        }
    }

    /// <summary>Running totals for one parse.</summary>
    private sealed class ParseCounters
    {
        public int Total { get; set; }

        public int Invalid { get; set; }

        public int DuplicatesExisting { get; set; }

        public int DuplicatesInFile { get; set; }
    }

    private sealed record StatusCount(RowStatus Status, int Count);

    private sealed record FailedProjection(
        int RowNumber,
        List<string> Values,
        ImportErrorCode? ErrorCode,
        string? Error);
}
