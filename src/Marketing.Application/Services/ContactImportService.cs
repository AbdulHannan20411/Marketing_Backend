using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IContactImportService" />
public sealed class ContactImportService : IContactImportService
{
    /// <summary>Rows shown in the preview, enough to confirm a mapping without shipping the file back.</summary>
    private const int SampleSize = 10;

    /// <summary>Cap on invalid rows named in the preview.</summary>
    private const int MaxPreviewErrors = 50;

    /// <summary>Cap on per-row errors in the result, so a wholly broken file returns a bounded payload.</summary>
    private const int MaxReportedErrors = 100;

    /// <summary>Upper bound on rows in one upload.</summary>
    public const int MaxRows = 50_000;

    private readonly IRepository<ContactImportBatch> _batches;
    private readonly IRepository<ContactImportRow> _rows;
    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<ContactTagAssignment> _tagAssignments;
    private readonly IRepository<ContactGroupMember> _groupMembers;
    private readonly IRepository<ContactTag> _tags;
    private readonly IRepository<ContactGroup> _groups;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly IPlanGuard _planGuard;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public ContactImportService(
        IRepository<ContactImportBatch> batches,
        IRepository<ContactImportRow> rows,
        IRepository<Contact> contacts,
        IRepository<ContactTagAssignment> tagAssignments,
        IRepository<ContactGroupMember> groupMembers,
        IRepository<ContactTag> tags,
        IRepository<ContactGroup> groups,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IPlanGuard planGuard,
        IDateTimeProvider clock)
    {
        _batches = batches;
        _rows = rows;
        _contacts = contacts;
        _tagAssignments = tagAssignments;
        _groupMembers = groupMembers;
        _tags = tags;
        _groups = groups;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _planGuard = planGuard;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ImportPreview> PreviewAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        var parsed = CsvReader.Parse(content);

        if (parsed.Count < 2)
        {
            throw new ValidationException("file", "The file needs a header row and at least one data row.");
        }

        var columns = parsed[0].Select(column => column.Trim()).ToList();
        var dataRows = parsed.Skip(1).ToList();

        if (dataRows.Count > MaxRows)
        {
            throw new ValidationException(
                "file",
                $"The file has {dataRows.Count:N0} rows. Split it into files of {MaxRows:N0} rows or fewer.");
        }

        var mapping = GuessMapping(columns);
        var tenantId = _tenantContext.RequireTenantId();

        // Every existing number is pulled once and matched in memory. A query per row would mean
        // fifty thousand round trips for a large import.
        var existingNumbers = new HashSet<string>(
            await _queries.ToListAsync(
                _contacts.Query().Select(contact => contact.NormalizedPhoneNumber),
                cancellationToken),
            StringComparer.Ordinal);

        var phoneIndex = IndexOf(columns, mapping.PhoneNumber);

        var batch = new ContactImportBatch
        {
            TenantId = tenantId,
            FileName = fileName,
            Columns = columns,
            TotalRows = dataRows.Count,
            UploadedOn = _clock.UtcNow,
            Status = ContactImportStatus.AwaitingMapping,
        };

        // Numbers repeated inside the file itself are counted separately from ones that collide
        // with stored contacts: the operator resolves them differently, and the wizard shows both.
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var duplicatesInFile = 0;
        var duplicatesExisting = 0;
        var invalidRows = new List<ImportRowError>();
        var invalid = 0;

        for (var index = 0; index < dataRows.Count; index++)
        {
            var values = dataRows[index];
            var rowNumber = index + 2; // +2: one-based, and the header occupies row one.
            var rawPhone = phoneIndex >= 0 && phoneIndex < values.Count ? values[phoneIndex] : null;
            var normalized = PhoneNumbers.Normalise(rawPhone);

            string? error = null;
            var isDuplicate = false;

            if (!PhoneNumbers.IsPlausible(rawPhone))
            {
                error = "Missing or invalid phone number.";
                invalid++;

                if (invalidRows.Count < MaxPreviewErrors)
                {
                    invalidRows.Add(new ImportRowError(rowNumber, error));
                }
            }
            else if (existingNumbers.Contains(normalized))
            {
                isDuplicate = true;
                duplicatesExisting++;
            }
            else if (!seenInFile.Add(normalized))
            {
                isDuplicate = true;
                duplicatesInFile++;
            }

            _rows.Add(new ContactImportRow
            {
                TenantId = tenantId,
                // Related by navigation: the batch is inserted in this same unit of work and has
                // no key yet, so assigning the foreign key would store a zero.
                ContactImportBatch = batch,
                RowNumber = rowNumber,
                Values = values,
                IsDuplicate = isDuplicate,
                Error = error,
            });
        }

        batch.DuplicateRows = duplicatesInFile + duplicatesExisting;
        batch.InvalidRows = invalid;

        _batches.Add(batch);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportPreview(
            PublicId.From(PublicId.Contact, batch.Id),
            fileName,
            dataRows.Count,
            columns,
            mapping,
            [.. dataRows.Take(SampleSize).Select(row => Keyed(columns, row))],
            duplicatesInFile,
            duplicatesExisting,
            invalidRows);
    }

    /// <inheritdoc />
    public async Task<ImportResult> CommitAsync(
        ImportCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var batch = await LoadBatchAsync(request.UploadId, tracked: true, cancellationToken);

        if (batch.Status == ContactImportStatus.Completed)
        {
            throw new BusinessRuleException("import_already_committed", "That import has already been committed.");
        }

        var indexes = ResolveIndexes(batch.Columns, request.Mapping);

        var rows = await _queries.ToListAsync(
            _rows.Query().Where(row => row.ContactImportBatchId == batch.Id).OrderBy(row => row.RowNumber),
            cancellationToken);

        var tenantId = batch.TenantId;

        var assignTagIds = await ResolveIdsAsync(
            _tags.Query().Select(tag => tag.Id), PublicId.Tag, request.AssignTagIds, cancellationToken);

        var assignGroupIds = await ResolveIdsAsync(
            _groups.Query().Select(group => group.Id), PublicId.Group, request.AssignGroupIds, cancellationToken);

        // Fetched once, then extended as the file introduces new names. Creating a tag per row
        // would mean a lookup per row for a file that usually names three tags in total.
        var tagsByName = await LoadTagsByNameAsync(cancellationToken);
        var groupsByName = await LoadGroupsByNameAsync(cancellationToken);

        var stored = (await _queries.ToListAsync(_contacts.Query(asNoTracking: false), cancellationToken))
            .ToDictionary(contact => contact.NormalizedPhoneNumber, StringComparer.Ordinal);

        // The plan ceiling stops the import; it does not fail it. An operator who has waited for a
        // 40,000-row upload should get the 12,000 that fit, not an error and nothing.
        var capacity = await _planGuard.RemainingContactCapacityAsync(cancellationToken);

        var outcome = new ImportOutcome();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            outcome.Processed++;

            if (row.Error is not null)
            {
                outcome.Fail(row.RowNumber, row.Error);

                continue;
            }

            var rawPhone = Cell(row.Values, indexes.Phone);
            var normalized = PhoneNumbers.Normalise(rawPhone);

            if (stored.TryGetValue(normalized, out var existing))
            {
                if (request.DuplicateStrategy != DuplicateStrategyOnImport.Update)
                {
                    // Create behaves as skip here on purpose: the unique index would reject a second
                    // row with this number, so honouring "create" would fail the whole transaction.
                    outcome.Skipped++;

                    continue;
                }

                ApplyRow(existing, row, indexes, request, _clock.UtcNow);
                outcome.Updated++;

                AssignAll(existing, tenantId, assignTagIds, assignGroupIds);
                AssignNamed(existing, tenantId, row, indexes, tagsByName, groupsByName);

                continue;
            }

            if (capacity <= 0)
            {
                outcome.Skipped++;
                outcome.Fail(row.RowNumber, "Your plan's contact limit was reached before this row.");

                continue;
            }

            var contact = new Contact
            {
                TenantId = tenantId,
                FullName = string.Empty,
                PhoneNumber = PhoneNumbers.ToDisplayForm(rawPhone),
                NormalizedPhoneNumber = normalized,
                Status = request.DefaultStatus,

                // Imported contacts carry no opt-in timestamp unless the file gives a status the
                // operator vouches for: the platform has no evidence of when consent was given, and
                // inventing one would fabricate a compliance record.
                OptedInAt = null,
            };

            ApplyRow(contact, row, indexes, request, _clock.UtcNow);

            _contacts.Add(contact);
            stored[normalized] = contact;
            capacity--;
            outcome.Created++;

            AssignAll(contact, tenantId, assignTagIds, assignGroupIds);
            AssignNamed(contact, tenantId, row, indexes, tagsByName, groupsByName);
        }

        batch.Status = ContactImportStatus.Completed;
        batch.CommittedOn = _clock.UtcNow;
        batch.ImportedCount = outcome.Created;
        batch.UpdatedCount = outcome.Updated;
        batch.SkippedCount = outcome.Skipped;
        batch.FailedCount = outcome.Failed;
        batch.RowErrors = [.. outcome.Errors.Select(error => $"{error.RowNumber}: {error.Reason}")];

        // One transaction for the whole batch. A partial import would leave the operator with no
        // way to tell which half landed.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Describe(batch, outcome.Errors);
    }

    /// <inheritdoc />
    public async Task<ImportResult> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var batch = await LoadBatchAsync(jobId, tracked: false, cancellationToken);

        return Describe(batch, ParseErrors(batch.RowErrors));
    }

    /// <summary>Copies the mapped cells of one staged row onto a contact.</summary>
    private static void ApplyRow(
        Contact contact,
        ContactImportRow row,
        ColumnIndexes indexes,
        ImportCommitRequest request,
        DateTimeOffset utcNow)
    {
        var rawPhone = Cell(row.Values, indexes.Phone) ?? contact.PhoneNumber;

        if (Cell(row.Values, indexes.Name) is { Length: > 0 } name)
        {
            contact.FullName = name.Trim();
        }
        else if (string.IsNullOrEmpty(contact.FullName))
        {
            // A contact with no name still has to render in a list, and the number is the only
            // thing guaranteed to be there.
            contact.FullName = PhoneNumbers.ToDisplayForm(rawPhone);
        }

        if (Cell(row.Values, indexes.Email) is { Length: > 0 } email && MailIsUsable(email))
        {
            contact.Email = email.Trim();
        }

        // An unrecognised country in an imported file is left blank rather than refused. The row is
        // otherwise usable, and refusing the whole import over a spelling is not a trade the
        // operator would make.
        var country = Cell(row.Values, indexes.Country);

        contact.Country = Countries.ToStorageCode(country)
                          ?? Countries.FromPhoneNumber(rawPhone)
                          ?? contact.Country;

        var status = Cell(row.Values, indexes.Status);

        contact.Status = Enum.TryParse<ContactStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : request.DefaultStatus;

        // The stamp records when this platform started treating them as subscribed - not a claim
        // about when consent was originally given, which an imported file cannot evidence. It is
        // recorded rather than left null so "subscribed implies an opt-in date" stays true.
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

    private void AssignAll(
        Contact contact,
        long? tenantId,
        List<long> tagIds,
        List<long> groupIds)
    {
        foreach (var tagId in tagIds)
        {
            _tagAssignments.Add(new ContactTagAssignment
            {
                TenantId = tenantId,
                Contact = contact,
                ContactTagId = tagId,
            });
        }

        foreach (var groupId in groupIds)
        {
            _groupMembers.Add(new ContactGroupMember
            {
                TenantId = tenantId,
                Contact = contact,
                ContactGroupId = groupId,
            });
        }
    }

    private async Task<ContactImportBatch> LoadBatchAsync(
        string uploadId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Contact, uploadId, "import batch");

        var batch = tracked
            ? await _batches.GetForUpdateAsync(id, cancellationToken)
            : await _queries.FirstOrDefaultAsync(
                _batches.Query().Where(candidate => candidate.Id == id),
                cancellationToken);

        return batch ?? throw new NotFoundException("Import batch", uploadId);
    }

    private static ImportResult Describe(ContactImportBatch batch, IReadOnlyList<ImportRowError> errors) =>
        new(
            PublicId.From(PublicId.Contact, batch.Id),
            batch.Status == ContactImportStatus.Completed ? ImportJobStatus.Completed : ImportJobStatus.Queued,
            batch.Status == ContactImportStatus.Completed ? batch.TotalRows : 0,
            batch.TotalRows,
            batch.ImportedCount,
            batch.UpdatedCount,
            batch.SkippedCount,
            batch.FailedCount,
            errors,
            batch.CommittedOn);

    private static ColumnIndexes ResolveIndexes(List<string> columns, ImportColumnMapping mapping)
    {
        var phone = IndexOf(columns, mapping.PhoneNumber);

        if (phone < 0)
        {
            throw new ValidationException(
                "mapping.phoneNumber",
                "Choose which column holds the phone number. It is the only required field.");
        }

        return new ColumnIndexes(
            phone,
            IndexOf(columns, mapping.FullName),
            IndexOf(columns, mapping.Email),
            IndexOf(columns, mapping.Country),
            IndexOf(columns, mapping.Status),
            IndexOf(columns, mapping.Tags),
            IndexOf(columns, mapping.Groups));
    }

    /// <summary>
    /// Guesses which column is which from the header names.
    /// <para>
    /// A convenience, not a decision: the operator confirms or changes it before committing. It
    /// exists because the common case is a well-labelled export, and making someone map seven
    /// obvious columns by hand every time is friction for no benefit.
    /// </para>
    /// </summary>
    private static ImportColumnMapping GuessMapping(List<string> columns)
    {
        return new ImportColumnMapping(
            Match(columns, ["name", "full name", "fullname", "contact", "customer"]),
            Match(columns, ["phone", "phone number", "phonenumber", "mobile", "msisdn", "number"]),
            Match(columns, ["email", "e-mail", "email address"]),
            Match(columns, ["country", "country code", "iso"]),
            Match(columns, ["status", "consent", "subscription"]),
            Match(columns, ["tags", "tag", "labels"]),
            Match(columns, ["groups", "group", "segments", "lists"]));

        static string? Match(List<string> columns, string[] candidates) =>
            columns.FirstOrDefault(column =>
                candidates.Any(candidate =>
                    string.Equals(column.Trim(), candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, string> Keyed(List<string> columns, List<string> values)
    {
        var keyed = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < columns.Count; index++)
        {
            keyed[columns[index]] = index < values.Count ? values[index] : string.Empty;
        }

        return keyed;
    }

    private static IEnumerable<string> SplitList(string? cell) =>
        string.IsNullOrWhiteSpace(cell)
            ? []
            : cell.Split(ImportDelimiters.List, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool MailIsUsable(string email) =>
        System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _);

    private static int IndexOf(List<string> columns, string? columnName) =>
        string.IsNullOrWhiteSpace(columnName)
            ? -1
            : columns.FindIndex(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));

    private static string? Cell(List<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : null;

    private static List<ImportRowError> ParseErrors(List<string> stored)
    {
        var errors = new List<ImportRowError>(stored.Count);

        foreach (var entry in stored)
        {
            var separator = entry.IndexOf(':', StringComparison.Ordinal);

            if (separator > 0 && int.TryParse(entry[..separator], out var rowNumber))
            {
                errors.Add(new ImportRowError(rowNumber, entry[(separator + 2)..]));
            }
        }

        return errors;
    }

    private async Task<Dictionary<string, ContactTag>> LoadTagsByNameAsync(CancellationToken cancellationToken)
    {
        var rows = await _queries.ToListAsync(_tags.Query(asNoTracking: false), cancellationToken);

        // Entities rather than keys, and tracked. A tag the file introduces is inserted in the
        // same unit of work and has no key until then, so callers relate to it by navigation.
        // Case-insensitive, matching the partial unique index: a file naming "VIP" must find the
        // stored "vip" rather than creating a second tag the operator then has to merge.
        return rows.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, ContactGroup>> LoadGroupsByNameAsync(
        CancellationToken cancellationToken)
    {
        var rows = await _queries.ToListAsync(_groups.Query(asNoTracking: false), cancellationToken);

        return rows.ToDictionary(group => group.Name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<long>> ResolveIdsAsync(
        IQueryable<long> source,
        string prefix,
        IReadOnlyList<string>? ids,
        CancellationToken cancellationToken)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }

        var parsed = new List<long>(ids.Count);

        foreach (var id in ids)
        {
            if (PublicId.TryParse(prefix, id, out var value))
            {
                parsed.Add(value);
            }
        }

        return [.. await _queries.ToListAsync(source.Where(existing => parsed.Contains(existing)), cancellationToken)];
    }

    private sealed record NamedKey(string Name, long Id);

    private sealed record ColumnIndexes(int Phone, int Name, int Email, int Country, int Status, int Tags, int Groups);

    /// <summary>Running totals for one commit.</summary>
    private sealed class ImportOutcome
    {
        public int Processed { get; set; }

        public int Created { get; set; }

        public int Updated { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; private set; }

        public List<ImportRowError> Errors { get; } = [];

        public void Fail(int rowNumber, string reason)
        {
            Failed++;

            if (Errors.Count < MaxReportedErrors)
            {
                Errors.Add(new ImportRowError(rowNumber, reason));
            }
        }
    }
}
