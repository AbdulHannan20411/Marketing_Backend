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
    private const int SampleSize = 5;

    /// <summary>Cap on returned per-row errors, so a wholly broken file cannot return a huge payload.</summary>
    private const int MaxReportedErrors = 50;

    /// <summary>Upper bound on rows in one upload.</summary>
    private const int MaxRows = 50_000;

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
            Id = SequentialGuid.Create(),
            TenantId = tenantId,
            FileName = fileName,
            Columns = columns,
            TotalRows = dataRows.Count,
            UploadedOn = _clock.UtcNow,
            Status = ContactImportStatus.AwaitingMapping,
        };

        // Numbers repeated inside the file itself count as duplicates too. Without this, a file
        // listing the same customer twice inserts one and then fails the unique index on the other.
        var seenInFile = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var invalid = 0;

        for (var index = 0; index < dataRows.Count; index++)
        {
            var values = dataRows[index];
            var rawPhone = phoneIndex >= 0 && phoneIndex < values.Count ? values[phoneIndex] : null;
            var normalized = PhoneNumbers.Normalise(rawPhone);

            string? error = null;
            var isDuplicate = false;

            if (!PhoneNumbers.IsPlausible(rawPhone))
            {
                error = "Missing or invalid phone number.";
                invalid++;
            }
            else if (existingNumbers.Contains(normalized) || !seenInFile.Add(normalized))
            {
                isDuplicate = true;
                duplicates++;
            }

            _rows.Add(new ContactImportRow
            {
                Id = SequentialGuid.Create(),
                TenantId = tenantId,
                ContactImportBatchId = batch.Id,
                RowNumber = index + 2, // +2: one-based, and the header occupies row one.
                Values = values,
                IsDuplicate = isDuplicate,
                Error = error,
            });
        }

        batch.DuplicateRows = duplicates;
        batch.InvalidRows = invalid;

        _batches.Add(batch);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportPreview(
            PublicId.From(PublicId.Contact, batch.Id),
            fileName,
            columns,
            [.. dataRows.Take(SampleSize).Select(row => (IReadOnlyList<string>)row)],
            dataRows.Count,
            duplicates,
            invalid,
            mapping);
    }

    /// <inheritdoc />
    public async Task<ImportResult> CommitAsync(
        string batchId,
        CommitImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Contact, batchId, "import batch");

        var batch = await _batches.GetForUpdateAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Import batch", batchId);

        if (batch.Status == ContactImportStatus.Committed)
        {
            throw new BusinessRuleException("import_already_committed", "That import has already been committed.");
        }

        var phoneIndex = IndexOf(batch.Columns, request.Mapping.PhoneNumber);

        if (phoneIndex < 0)
        {
            throw new ValidationException(
                "mapping.phoneNumber",
                "Choose which column holds the phone number. It is the only required field.");
        }

        var nameIndex = IndexOf(batch.Columns, request.Mapping.FullName);
        var emailIndex = IndexOf(batch.Columns, request.Mapping.Email);
        var countryIndex = IndexOf(batch.Columns, request.Mapping.Country);

        var rows = await _queries.ToListAsync(
            _rows.Query().Where(row => row.ContactImportBatchId == batch.Id).OrderBy(row => row.RowNumber),
            cancellationToken);

        var tenantId = batch.TenantId;
        var tagIds = await ResolveIdsAsync(_tags.Query().Select(tag => tag.Id), PublicId.Tag, request.TagIds, cancellationToken);
        var groupIds = await ResolveIdsAsync(_groups.Query().Select(group => group.Id), PublicId.Group, request.GroupIds, cancellationToken);

        var imported = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<ImportRowError>();

        foreach (var row in rows)
        {
            if (row.Error is not null)
            {
                failed++;

                if (errors.Count < MaxReportedErrors)
                {
                    errors.Add(new ImportRowError(row.RowNumber, row.Error));
                }

                continue;
            }

            if (row.IsDuplicate && request.SkipDuplicates)
            {
                skipped++;
                continue;
            }

            var rawPhone = Cell(row.Values, phoneIndex);
            var normalized = PhoneNumbers.Normalise(rawPhone);

            var contact = new Contact
            {
                Id = SequentialGuid.Create(),
                TenantId = tenantId,
                FullName = Cell(row.Values, nameIndex) is { Length: > 0 } name
                    ? name.Trim()
                    // A contact with no name still has to render in a list, and the number is the
                    // only thing guaranteed to be there.
                    : PhoneNumbers.ToDisplayForm(rawPhone),
                PhoneNumber = PhoneNumbers.ToDisplayForm(rawPhone),
                NormalizedPhoneNumber = normalized,
                Email = Cell(row.Values, emailIndex) is { Length: > 0 } email ? email.Trim() : null,
                Country = Cell(row.Values, countryIndex)?.Trim().ToUpperInvariant() ?? string.Empty,

                // Imported contacts arrive subscribed but with no opt-in timestamp: the platform
                // has no evidence of when or whether consent was given, and inventing one would be
                // fabricating a compliance record.
                Status = ContactStatus.Subscribed,
                OptedInAt = null,
            };

            _contacts.Add(contact);

            foreach (var tagId in tagIds)
            {
                _tagAssignments.Add(new ContactTagAssignment
                {
                    Id = SequentialGuid.Create(),
                    TenantId = tenantId,
                    ContactId = contact.Id,
                    ContactTagId = tagId,
                });
            }

            foreach (var groupId in groupIds)
            {
                _groupMembers.Add(new ContactGroupMember
                {
                    Id = SequentialGuid.Create(),
                    TenantId = tenantId,
                    ContactId = contact.Id,
                    ContactGroupId = groupId,
                });
            }

            imported++;
        }

        batch.Status = ContactImportStatus.Committed;
        batch.CommittedOn = _clock.UtcNow;
        batch.ImportedCount = imported;

        // One transaction for the whole batch. A partial import would leave the operator with no
        // way to tell which half landed.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportResult(imported, skipped, failed, errors);
    }

    /// <summary>
    /// Guesses which column is which from the header names.
    /// <para>
    /// A convenience, not a decision: the operator confirms or changes it before committing. It
    /// exists because the common case is a well-labelled export, and making someone map four
    /// obvious columns by hand every time is friction for no benefit.
    /// </para>
    /// </summary>
    private static ImportColumnMapping GuessMapping(List<string> columns)
    {
        return new ImportColumnMapping(
            Match(columns, ["name", "full name", "fullname", "contact", "customer"]),
            Match(columns, ["phone", "phone number", "phonenumber", "mobile", "msisdn", "number"]),
            Match(columns, ["email", "e-mail", "email address"]),
            Match(columns, ["country", "country code", "iso"]));

        static string? Match(List<string> columns, string[] candidates) =>
            columns.FirstOrDefault(column =>
                candidates.Any(candidate =>
                    string.Equals(column.Trim(), candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static int IndexOf(List<string> columns, string? columnName) =>
        string.IsNullOrWhiteSpace(columnName)
            ? -1
            : columns.FindIndex(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));

    private static string? Cell(List<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : null;

    private async Task<List<Guid>> ResolveIdsAsync(
        IQueryable<Guid> source,
        string prefix,
        IReadOnlyList<string>? ids,
        CancellationToken cancellationToken)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }

        var parsed = new List<Guid>(ids.Count);

        foreach (var id in ids)
        {
            if (PublicId.TryParse(prefix, id, out var value))
            {
                parsed.Add(value);
            }
        }

        return [.. await _queries.ToListAsync(source.Where(existing => parsed.Contains(existing)), cancellationToken)];
    }
}
