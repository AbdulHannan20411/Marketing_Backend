using System.Text;
using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Imports;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Imports;

/// <summary>
/// The command side of contact importing.
/// <para>
/// Every method here returns as soon as the intent is recorded. The reading, validating, writing
/// and reporting all happen in workers, because a 50,000-row file takes minutes and an HTTP request
/// that takes minutes is one a proxy, a load balancer or a laptop lid will end for you.
/// </para>
/// </summary>
public interface IImportService
{
    /// <summary>Accepts a file and queues it for reading.</summary>
    /// <param name="command">The upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ValidationException">The file is empty, too large, or of a type we cannot read.</exception>
    public Task<ImportUploadResponse> UploadAsync(
        ImportUploadCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Saves which file column feeds which contact field.</summary>
    /// <param name="batchId">Prefixed batch identifier.</param>
    /// <param name="map">The mapping. All seven keys; null means unmapped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportBatchDetail> SaveMappingAsync(
        string batchId,
        ImportColumnMap map,
        CancellationToken cancellationToken = default);

    /// <summary>Queues the writing of an import's rows as contacts.</summary>
    /// <param name="batchId">Prefixed batch identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportCommitResponse> CommitAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>Abandons an import that has not started writing.</summary>
    /// <param name="batchId">Prefixed batch identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportBatchDetail> CancelAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a workbook of the rows that could not be used.</summary>
    /// <param name="batchId">Prefixed batch identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportExportJob> RequestErrorExportAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns an export's progress.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ImportExportJob> GetExportAsync(
        string exportId,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a finished export for download.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="BusinessRuleException">The export is not finished.</exception>
    public Task<ImportDownload> OpenExportAsync(
        string exportId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the blank CSV an operator fills in.</summary>
    public ImportDownload Template();
}

/// <inheritdoc cref="IImportService" />
public sealed class ImportService : IImportService
{
    /// <summary>Container uploaded files are stored under.</summary>
    private const string UploadContainer = "contact-imports";

    /// <summary>Headers of the downloadable template, in the order the wizard expects them.</summary>
    private static readonly string[] TemplateColumns =
        ["Full Name", "Phone Number", "Email", "Country", "Status", "Tags", "Groups"];

    /// <summary>
    /// Filled-in example rows, so the expected format of each column is unambiguous.
    /// </summary>
    /// <remarks>
    /// Two rows, from different countries, and both numbers deliberately in full international form
    /// with the country code.
    /// <para>
    /// This is the cheapest fix available for a whole class of failure. A file that opens showing
    /// only a US number teaches nothing to somebody in a country where people write
    /// <c>0336 7890092</c>; that number imports without complaint, looks correct on every screen,
    /// and is then refused by Meta at send time. The second row exists to make the country code
    /// look like part of the format rather than part of the example.
    /// </para>
    /// </remarks>
    private static readonly string[][] TemplateExamples =
    [
        ["Jane Doe", "+14155552671", "jane@example.com", "US", "Subscribed", "vip;newsletter", "Customers"],
        ["Ayesha Khan", "+923367890092", "ayesha@example.com", "PK", "Subscribed", "vip", "Customers"],
    ];

    private readonly IRepository<ContactImportBatch> _batches;
    private readonly IRepository<ContactImportExport> _exports;
    private readonly IImportHistoryService _history;
    private readonly IImportJobDispatcher _dispatcher;
    private readonly IImportFileReaderFactory _readers;
    private readonly IFileStorage _storage;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly ImportOptions _options;

    /// <summary>Initialises a new instance.</summary>
    public ImportService(
        IRepository<ContactImportBatch> batches,
        IRepository<ContactImportExport> exports,
        IImportHistoryService history,
        IImportJobDispatcher dispatcher,
        IImportFileReaderFactory readers,
        IFileStorage storage,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        IDateTimeProvider clock,
        IOptions<ImportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _batches = batches;
        _exports = exports;
        _history = history;
        _dispatcher = dispatcher;
        _readers = readers;
        _storage = storage;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<ImportUploadResponse> UploadAsync(
        ImportUploadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Validate(command);

        var tenantId = _tenantContext.RequireTenantId();
        var uploadedOn = _clock.UtcNow;

        // Stored before the row exists, because the row has to carry the key. An upload that
        // fails afterwards leaves a file with no row — harmless, and swept up by the cleanup job.
        // The reverse order would leave a row pointing at nothing, which the worker cannot recover
        // from and the operator cannot retry.
        var storageKey = await _storage.SaveAsync(
            UploadContainer, command.FileName, command.Content, cancellationToken);

        var batch = new ContactImportBatch
        {
            TenantId = tenantId,
            FileName = command.FileName,
            FileSizeBytes = command.SizeBytes,
            StorageKey = storageKey,
            DuplicateStrategy = command.DuplicateStrategy,
            Status = ContactImportStatus.Queued,
            UploadedOn = uploadedOn,
            UploadedByUserId = _currentUser.AuditUserId,
            UploadedByName = _currentUser.DisplayName,
        };

        _batches.Add(batch);

        // Queued in the same unit of work as the batch, so there is no instant at which an import
        // exists with no work scheduled against it.
        _dispatcher.Enqueue(batch, ImportJobKind.Process, _currentUser.AuditUserId);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportUploadResponse(
            PublicId.From(PublicId.ImportBatch, batch.Id),
            batch.FileName,
            batch.FileSizeBytes,
            BatchStatus.Pending,
            batch.UploadedOn);
    }

    /// <inheritdoc />
    public async Task<ImportBatchDetail> SaveMappingAsync(
        string batchId,
        ImportColumnMap map,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        var batch = await LoadForUpdateAsync(batchId, cancellationToken);

        if (batch.Status is not (ContactImportStatus.AwaitingMapping or ContactImportStatus.AwaitingConfirmation))
        {
            throw new BusinessRuleException(
                "import_not_mappable",
                "The mapping can only be changed while the import is waiting to be confirmed.");
        }

        EnsureMapped(map, batch.Columns);

        batch.ColumnMapping = ImportMapping.ToJson(map);

        // A mapped batch is one step further on than an unmapped one. The distinction is internal —
        // the client is shown one waiting state — but the commit refuses to run without it.
        if (batch.Status == ContactImportStatus.AwaitingMapping)
        {
            ImportStateMachine.Transition(batch, ContactImportStatus.AwaitingConfirmation);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await _history.GetBatchAsync(batchId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ImportCommitResponse> CommitAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await LoadForUpdateAsync(batchId, cancellationToken);

        if (batch.Status == ContactImportStatus.AwaitingMapping)
        {
            throw new BusinessRuleException(
                "import_not_mapped",
                "Choose which column holds each field before importing.");
        }

        if (batch.Status != ContactImportStatus.AwaitingConfirmation)
        {
            throw new BusinessRuleException(
                "import_not_committable",
                "That import is not waiting to be confirmed.");
        }

        var queuedAt = _clock.UtcNow;

        ImportStateMachine.Transition(batch, ContactImportStatus.Committing);

        // Reset before the worker starts, so a second run after a dead-lettered first one reports
        // its own counters rather than adding to a half-finished set.
        batch.ProcessedRows = 0;

        _dispatcher.Enqueue(batch, ImportJobKind.Commit, _currentUser.AuditUserId);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportCommitResponse(
            PublicId.From(PublicId.ImportBatch, batch.Id),
            BatchStatus.Committing,
            queuedAt);
    }

    /// <inheritdoc />
    public async Task<ImportBatchDetail> CancelAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await LoadForUpdateAsync(batchId, cancellationToken);

        if (!ImportStateMachine.CanTransition(batch.Status, ContactImportStatus.Cancelled))
        {
            // Deliberately not cancellable once writing has begun: contacts are already in the
            // tenant's audience by then, and "cancelled" would misdescribe what happened.
            throw new BusinessRuleException(
                "import_not_cancellable",
                batch.Status == ContactImportStatus.Committing
                    ? "That import is already writing contacts and can no longer be cancelled."
                    : "That import has already finished.");
        }

        ImportStateMachine.Transition(batch, ContactImportStatus.Cancelled);

        batch.CommittedOn = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await _history.GetBatchAsync(batchId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ImportExportJob> RequestErrorExportAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var batch = await LoadForUpdateAsync(batchId, cancellationToken);

        if (batch.FailedCount <= 0)
        {
            throw new BusinessRuleException(
                "import_has_no_failures",
                "Every row in that import was used. There is nothing to export.");
        }

        var requestedAt = _clock.UtcNow;

        var export = new ContactImportExport
        {
            TenantId = batch.TenantId,
            ContactImportBatch = batch,
            Status = ExportStatus.Pending,
            FileName = FailedRecordsFileName(batch.FileName),
            RequestedByUserId = _currentUser.AuditUserId,
            RequestedAt = requestedAt,
        };

        _exports.Add(export);
        _dispatcher.EnqueueExport(export, _currentUser.AuditUserId);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Describe(export, batch.Id);
    }

    /// <inheritdoc />
    public async Task<ImportExportJob> GetExportAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        var export = await LoadExportAsync(exportId, cancellationToken);

        return Describe(export, export.ContactImportBatchId);
    }

    /// <inheritdoc />
    public async Task<ImportDownload> OpenExportAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        var export = await LoadExportAsync(exportId, cancellationToken);

        if (export.Status != ExportStatus.Completed || string.IsNullOrEmpty(export.StorageKey))
        {
            throw new BusinessRuleException(
                "export_not_ready",
                export.Status == ExportStatus.Failed
                    ? "That export could not be generated. Ask for it again."
                    : "That export is still being generated.");
        }

        // The key comes from a row this tenant owns, never from the request, so a caller cannot
        // steer the read at another tenant's file by editing an identifier.
        var content = await _storage.OpenAsync(export.StorageKey, cancellationToken);

        return new ImportDownload(content, export.FileName, ContentTypes.Xlsx);
    }

    /// <inheritdoc />
    public ImportDownload Template()
    {
        var csv = new StringBuilder();

        csv.AppendLine(string.Join(',', TemplateColumns));

        foreach (var example in TemplateExamples)
        {
            csv.AppendLine(string.Join(',', example));
        }

        // A byte-order mark, purely so Excel opens the file as UTF-8. Without it, a name with an
        // accent in it renders as mojibake the first time an operator opens the template.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(csv.ToString()))
            .ToArray();

        return new ImportDownload(new MemoryStream(bytes, writable: false), "contact-import-template.csv", ContentTypes.Csv);
    }

    /// <summary>Refuses an upload we cannot or should not read, before anything is stored.</summary>
    private void Validate(ImportUploadCommand command)
    {
        if (command.SizeBytes <= 0)
        {
            throw new ValidationException("file", "Choose a file to import.");
        }

        if (command.SizeBytes > _options.MaxFileSizeBytes)
        {
            throw new ValidationException(
                "file",
                $"That file is larger than the {_options.MaxFileSizeBytes / (1024 * 1024)} MB limit.");
        }

        // Throws when no reader handles the extension, which is also the accepted-types check.
        _readers.For(command.FileName);
    }

    /// <summary>Refuses a mapping that names a column the file does not have, or omits the number.</summary>
    private static void EnsureMapped(ImportColumnMap map, IReadOnlyList<string> columns)
    {
        if (string.IsNullOrWhiteSpace(map.PhoneNumber))
        {
            throw new ValidationException(
                "mapping.phoneNumber",
                "Choose which column holds the phone number. It is the only required field.");
        }

        foreach (var (field, column) in Named(map))
        {
            if (column is { Length: > 0 }
                && !columns.Any(candidate => string.Equals(candidate, column, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ValidationException($"mapping.{field}", $"The file has no column called \"{column}\".");
            }
        }
    }

    /// <summary>The mapping's entries, paired with the wire name of each field.</summary>
    private static IEnumerable<(string Field, string? Column)> Named(ImportColumnMap map) =>
    [
        ("fullName", map.FullName),
        ("phoneNumber", map.PhoneNumber),
        ("email", map.Email),
        ("country", map.Country),
        ("status", map.Status),
        ("tags", map.Tags),
        ("groups", map.Groups),
    ];

    /// <summary>Loads a tracked batch, or reports it missing.</summary>
    private async Task<ContactImportBatch> LoadForUpdateAsync(string batchId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.ImportBatch, batchId, "import");

        return await _batches.GetForUpdateAsync(id, cancellationToken)
               ?? throw new NotFoundException("Import", batchId);
    }

    /// <summary>Loads an export, or reports it missing.</summary>
    private async Task<ContactImportExport> LoadExportAsync(string exportId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Export, exportId, "export");

        return await _queries.FirstOrDefaultAsync(
            _exports.Query().Where(candidate => candidate.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Export", exportId);
    }

    /// <summary>Names the workbook after the file it reports on.</summary>
    private static string FailedRecordsFileName(string source) =>
        $"{Path.GetFileNameWithoutExtension(source)}-failed-records.xlsx";

    private static ImportExportJob Describe(ContactImportExport export, long batchId) =>
        new(
            PublicId.From(PublicId.Export, export.Id),
            PublicId.From(PublicId.ImportBatch, batchId),
            export.Status,
            export.FileName,
            export.RowCount,
            export.RequestedAt,
            export.CompletedAt,
            export.FailureReason);
}
