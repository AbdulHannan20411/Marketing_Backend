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

    /// <summary>Staged rows.</summary>
    public ICollection<ContactImportRow> Rows { get; set; } = [];
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

    /// <summary>Batch navigation.</summary>
    public ContactImportBatch ContactImportBatch { get; set; } = null!;
}
