using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One CSV upload, held between the preview call and the commit call.
/// <para>
/// Staged in the database rather than a cache because the wizard is a two-step flow with a human
/// in the middle: the rows have to survive a restart, a deploy, and however long the operator
/// spends mapping columns. A fail-open cache would silently lose a 50,000-row upload.
/// </para>
/// </summary>
public sealed class ContactImportBatch : BaseEntity, IRequiresTenant
{
    /// <summary>Name of the uploaded file, shown back to the operator.</summary>
    public required string FileName { get; set; }

    /// <summary>Column headers detected in the file, in file order.</summary>
    public List<string> Columns { get; set; } = [];

    /// <summary>Data rows parsed, excluding the header.</summary>
    public int TotalRows { get; set; }

    /// <summary>Rows whose phone number already exists in this tenant.</summary>
    public int DuplicateRows { get; set; }

    /// <summary>Rows that could not be parsed.</summary>
    public int InvalidRows { get; set; }

    /// <summary>Whether the batch has been committed.</summary>
    public ContactImportStatus Status { get; set; } = ContactImportStatus.AwaitingMapping;

    /// <summary>Instant the file was uploaded.</summary>
    public DateTimeOffset UploadedOn { get; set; }

    /// <summary>Instant the batch was committed.</summary>
    public DateTimeOffset? CommittedOn { get; set; }

    /// <summary>Contacts created by the commit.</summary>
    public int ImportedCount { get; set; }

    /// <summary>Existing contacts the commit updated.</summary>
    public int UpdatedCount { get; set; }

    /// <summary>
    /// Rows the commit deliberately passed over — duplicates the operator chose to skip, and rows
    /// beyond the plan's contact ceiling.
    /// </summary>
    public int SkippedCount { get; set; }

    /// <summary>Rows the commit could not use.</summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Why individual rows failed, so the poll endpoint can report them after the commit has
    /// returned. Capped when written.
    /// </summary>
    public List<string> RowErrors { get; set; } = [];

    /// <summary>
    /// Key the uploaded file is stored under.
    /// <para>
    /// The file lives in <c>IFileStorage</c>, not here. A worker reads it minutes after the upload
    /// and possibly on another machine, so the row holds a reference rather than the bytes.
    /// </para>
    /// </summary>
    public string? StorageKey { get; set; }

    /// <summary>Key of the generated failed-record report, once one has been asked for.</summary>
    public string? ErrorReportStorageKey { get; set; }

    /// <summary>
    /// Which file column feeds which contact field, as JSON.
    /// <para>
    /// Column <em>names</em>, never indexes: an index silently means a different field if the file
    /// is re-exported with a column inserted.
    /// </para>
    /// </summary>
    public string? ColumnMapping { get; set; }

    /// <summary>What the commit does with a row whose number already exists.</summary>
    public ImportDuplicateStrategy DuplicateStrategy { get; set; } = ImportDuplicateStrategy.Skip;

    /// <summary>Rows colliding with another row inside the same file.</summary>
    public int DuplicatesInFile { get; set; }

    /// <summary>Why processing failed, when it did.</summary>
    public string? FailureReason { get; set; }

    /// <summary>User who uploaded it, notified as the run progresses.</summary>
    public long UploadedByUserId { get; set; }

    /// <summary>
    /// Display name of the uploader, denormalised at upload time.
    /// <para>
    /// Copied rather than joined because the history list is read far more often than a name
    /// changes, and an import stays truthful about who ran it even after the account is renamed or
    /// removed.
    /// </para>
    /// </summary>
    public string? UploadedByName { get; set; }

    /// <summary>Size of the uploaded file, shown in the history list.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Rows the current stage has finished with, against <see cref="TotalRows"/>.
    /// <para>
    /// Recorded rather than inferred from the status, so the progress bar reports real progress
    /// instead of jumping between the handful of positions a status can express.
    /// </para>
    /// </summary>
    public int ProcessedRows { get; set; }

    /// <summary>Staged rows.</summary>
    public ICollection<ContactImportRow> Rows { get; set; } = [];

    /// <summary>Failed-record exports asked for against this batch.</summary>
    public ICollection<ContactImportExport> Exports { get; set; } = [];
}

/// <summary>One staged row from a CSV upload.</summary>
public sealed class ContactImportRow : BaseEntity, IRequiresTenant
{
    /// <summary>Batch the row belongs to.</summary>
    public long ContactImportBatchId { get; set; }

    /// <summary>One-based row number in the source file, used in the error report.</summary>
    public int RowNumber { get; set; }

    /// <summary>Raw cell values, positionally aligned with the batch's columns.</summary>
    public List<string> Values { get; set; } = [];

    /// <summary>
    /// Whether the row's phone number already exists in the tenant.
    /// <para>
    /// Determined at preview time so the operator sees the count before committing, and recorded
    /// per row so the commit can skip them without re-checking.
    /// </para>
    /// </summary>
    public bool IsDuplicate { get; set; }

    /// <summary>Why the row could not be used, when it could not.</summary>
    public string? Error { get; set; }

    /// <summary>What happened to the row, as the review table renders it.</summary>
    public RowStatus Status { get; set; } = RowStatus.Pending;

    /// <summary>
    /// Machine-readable reason the row could not be used.
    /// <para>
    /// Carried alongside <see cref="Error"/> rather than replacing it: the code is what the client
    /// groups and translates by, the text is the fallback for a code it does not yet know.
    /// </para>
    /// </summary>
    public ImportErrorCode? ErrorCode { get; set; }

    /// <summary>Contact field the failure concerns, when it concerns one.</summary>
    public string? ErrorField { get; set; }

    /// <summary>Batch navigation.</summary>
    public ContactImportBatch ContactImportBatch { get; set; } = null!;
}

/// <summary>
/// One request to build a workbook of an import's failed rows.
/// <para>
/// A row of its own rather than a column on the batch, because an operator may ask more than once —
/// after fixing some rows, or long after the file expired — and each attempt has its own status,
/// its own file and its own failure.
/// </para>
/// </summary>
public sealed class ContactImportExport : BaseEntity, IRequiresTenant
{
    /// <summary>Import the export covers.</summary>
    public long ContactImportBatchId { get; set; }

    /// <summary>Where generation has got to.</summary>
    public ExportStatus Status { get; set; } = ExportStatus.Pending;

    /// <summary>Name the download is served under.</summary>
    public required string FileName { get; set; }

    /// <summary>Key the generated workbook is stored under, once it exists.</summary>
    public string? StorageKey { get; set; }

    /// <summary>Rows written into the workbook.</summary>
    public int RowCount { get; set; }

    /// <summary>User who asked for it.</summary>
    public long RequestedByUserId { get; set; }

    /// <summary>Instant it was asked for.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Instant generation finished.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Why generation failed, when it did.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Batch navigation.</summary>
    public ContactImportBatch ContactImportBatch { get; set; } = null!;
}
