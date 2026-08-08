using System.Text.Json.Serialization;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Contacts;

/// <summary>Separators the import and export agree on.</summary>
public static class ImportDelimiters
{
    /// <summary>
    /// Separates several values inside one cell — the tags and groups columns.
    /// <para>
    /// A semicolon rather than a comma, because a comma inside a CSV cell forces quoting and the
    /// spreadsheet exports this has to read do not reliably quote.
    /// </para>
    /// </summary>
    public const string List = ";";
}

/// <summary>What to do with a row whose phone number already exists.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DuplicateStrategyOnImport>))]
public enum DuplicateStrategyOnImport
{
    /// <summary>Leave the stored contact untouched. The safe default.</summary>
    Skip,

    /// <summary>Apply the file's values over the stored contact.</summary>
    Update,

    /// <summary>
    /// Create anyway.
    /// <para>
    /// Refused for a collision with a stored contact, because the unique index would reject it;
    /// it only affects rows that collide with each other inside the file.
    /// </para>
    /// </summary>
    Create,
}

/// <summary>
/// What the first import call returns: enough for the operator to map columns and decide whether
/// to commit.
/// </summary>
/// <param name="UploadId">Identifier to pass to the commit call.</param>
/// <param name="FileName">Name of the uploaded file.</param>
/// <param name="TotalRows">Data rows found.</param>
/// <param name="DetectedColumns">Detected column headers, in file order.</param>
/// <param name="SuggestedMapping">
/// Best-guess column mapping from the headers, so the wizard opens with fields already filled in
/// for the common case of a well-labelled export.
/// </param>
/// <param name="SampleRows">First rows, keyed by column name so the client need not track order.</param>
/// <param name="DuplicatesInFile">Rows colliding with another row in the same file.</param>
/// <param name="DuplicatesExisting">Rows colliding with a stored contact.</param>
/// <param name="InvalidRows">Rows that could not be parsed, capped.</param>
public sealed record ImportPreview(
    string UploadId,
    string FileName,
    int TotalRows,
    IReadOnlyList<string> DetectedColumns,
    ImportColumnMapping SuggestedMapping,
    IReadOnlyList<IReadOnlyDictionary<string, string>> SampleRows,
    int DuplicatesInFile,
    int DuplicatesExisting,
    IReadOnlyList<ImportRowError> InvalidRows);

/// <summary>
/// Which column feeds which contact field.
/// <para>
/// Column <em>names</em> rather than indexes, because the operator picks from headers and an index
/// silently means something different if the file is re-exported with a column inserted.
/// </para>
/// </summary>
/// <param name="FullName">Column supplying the name.</param>
/// <param name="PhoneNumber">Column supplying the phone number. Required.</param>
/// <param name="Email">Column supplying the email address.</param>
/// <param name="Country">Column supplying the country.</param>
/// <param name="Status">Column supplying the consent state.</param>
/// <param name="Tags">Column supplying a delimited list of tag names.</param>
/// <param name="Groups">Column supplying a delimited list of group names.</param>
public sealed record ImportColumnMapping(
    string? FullName = null,
    string? PhoneNumber = null,
    string? Email = null,
    string? Country = null,
    string? Status = null,
    string? Tags = null,
    string? Groups = null);

/// <summary>Request to commit a staged import.</summary>
/// <param name="UploadId">Identifier from the preview call.</param>
/// <param name="Mapping">Column mapping chosen by the operator.</param>
/// <param name="DuplicateStrategy">
/// What to do with a row whose number already exists. Defaults to skipping, because updating is
/// destructive and should be a deliberate choice.
/// </param>
/// <param name="DefaultStatus">Consent state for rows with no status column.</param>
/// <param name="AssignTagIds">Tags applied to every imported row.</param>
/// <param name="AssignGroupIds">Groups every imported row joins.</param>
public sealed record ImportCommitRequest(
    string UploadId,
    ImportColumnMapping Mapping,
    DuplicateStrategyOnImport DuplicateStrategy = DuplicateStrategyOnImport.Skip,
    ContactStatus DefaultStatus = ContactStatus.Subscribed,
    IReadOnlyList<string>? AssignTagIds = null,
    IReadOnlyList<string>? AssignGroupIds = null);

/// <summary>Outcome of a committed import.</summary>
/// <param name="JobId">Identifier the outcome can be re-read with.</param>
/// <param name="Status">Where the job has got to.</param>
/// <param name="ProcessedRows">Rows examined.</param>
/// <param name="TotalRows">Rows in the file.</param>
/// <param name="Created">Contacts created.</param>
/// <param name="Updated">Existing contacts updated.</param>
/// <param name="Skipped">Rows deliberately passed over.</param>
/// <param name="Failed">Rows that could not be imported.</param>
/// <param name="Errors">Per-row failures, capped so a broken file cannot return a huge payload.</param>
/// <param name="CompletedAt">Instant the job finished.</param>
public sealed record ImportResult(
    string JobId,
    ImportJobStatus Status,
    int ProcessedRows,
    int TotalRows,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    IReadOnlyList<ImportRowError> Errors,
    DateTimeOffset? CompletedAt);

/// <summary>Where an import job has got to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportJobStatus>))]
public enum ImportJobStatus
{
    /// <summary>Uploaded and mapped, not yet committed.</summary>
    Queued,

    /// <summary>Being written.</summary>
    Processing,

    /// <summary>Finished. Rows may still have been skipped or failed.</summary>
    Completed,

    /// <summary>Abandoned before finishing.</summary>
    Failed,
}

/// <summary>One row that could not be imported.</summary>
/// <param name="RowNumber">One-based row number in the source file.</param>
/// <param name="Reason">Why it failed.</param>
public sealed record ImportRowError(int RowNumber, string Reason);
