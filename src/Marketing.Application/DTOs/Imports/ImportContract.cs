using System.Text.Json.Serialization;
using Marketing.Common.Requests;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Imports;

// Every enum in this file serialises PascalCase, overriding the API's global camelCase policy.
// The import contract specifies PascalCase wire values and the client matches on them exactly, so
// a converter without a naming policy is applied per type rather than changing the global one and
// breaking every other module.

/// <summary>Where an import batch has got to, as the client sees it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatchStatus>))]
public enum BatchStatus
{
    /// <summary>Accepted and stored; the parse has not started.</summary>
    Pending,

    /// <summary>A worker is reading and validating the file.</summary>
    Processing,

    /// <summary>Parsed. Waiting for the operator to confirm the mapping — the worker stops here.</summary>
    AwaitingMapping,

    /// <summary>A worker is writing contacts.</summary>
    Committing,

    /// <summary>Finished with every usable row written.</summary>
    Completed,

    /// <summary>Finished, but some rows could not be used.</summary>
    CompletedWithErrors,

    /// <summary>The file could not be read, or a worker gave up.</summary>
    Failed,

    /// <summary>Cancelled by the operator.</summary>
    Cancelled,
}

/// <summary>
/// Row counters for a batch.
/// </summary>
/// <remarks>
/// Disjoint by construction once an import finishes:
/// <c>totalRows = successful + updated + skipped + failed</c>. <c>duplicates</c> describes what the
/// parse observed and overlaps deliberately — a duplicate becomes a skip or an update depending on
/// the chosen strategy, and reporting both is what lets the operator see why a number moved.
/// </remarks>
/// <param name="TotalRows">Data rows in the file, excluding the header.</param>
/// <param name="Successful">Contacts created.</param>
/// <param name="Updated">Existing contacts updated.</param>
/// <param name="Duplicates">Rows whose number already existed.</param>
/// <param name="Failed">Rows that could not be used.</param>
/// <param name="Skipped">Rows deliberately passed over.</param>
public sealed record ImportStatistics(
    int TotalRows,
    int Successful,
    int Updated,
    int Duplicates,
    int Failed,
    int Skipped);

/// <summary>Which file column feeds which contact field. All seven keys are always present.</summary>
/// <param name="FullName">Column supplying the name. Required.</param>
/// <param name="PhoneNumber">Column supplying the phone number. Required.</param>
/// <param name="Email">Column supplying the email address.</param>
/// <param name="Country">Column supplying the country.</param>
/// <param name="Status">Column supplying the consent state.</param>
/// <param name="Tags">Column supplying a delimited list of tag names.</param>
/// <param name="Groups">Column supplying a delimited list of group names.</param>
public sealed record ImportColumnMap(
    string? FullName = null,
    string? PhoneNumber = null,
    string? Email = null,
    string? Country = null,
    string? Status = null,
    string? Tags = null,
    string? Groups = null);

/// <summary>How many rows failed for one reason, for the detail screen's summary.</summary>
/// <param name="Code">The failure.</param>
/// <param name="Count">Rows carrying it.</param>
public sealed record ImportErrorGroup(ImportErrorCode Code, int Count);

/// <summary>The plan ceiling, when one is relevant to this import.</summary>
/// <param name="ContactLimit">Contacts the plan allows.</param>
/// <param name="CurrentContacts">Contacts already stored.</param>
/// <param name="SkippedForLimit">Rows this import passed over because the ceiling was reached.</param>
public sealed record ImportPlanLimit(int ContactLimit, int CurrentContacts, int SkippedForLimit);

/// <summary>An import, as the history list renders it.</summary>
/// <param name="BatchId">Opaque identifier, prefixed <c>imp_</c>.</param>
/// <param name="FileName">Name of the uploaded file.</param>
/// <param name="FileSizeBytes">Size of the upload.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Statistics">Row counters.</param>
/// <param name="UploadedAt">Instant the file was accepted.</param>
/// <param name="CompletedAt">Instant it finished.</param>
/// <param name="UploadedBy">Display name of whoever uploaded it.</param>
/// <param name="HasFailedRecords">
/// Whether a failed-record export can be produced. False when there is nothing to export, including
/// once a generated file has expired — the action is hidden rather than offered and then failing.
/// </param>
public sealed record ImportBatchListItem(
    string BatchId,
    string FileName,
    long FileSizeBytes,
    BatchStatus Status,
    ImportStatistics Statistics,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CompletedAt,
    string UploadedBy,
    bool HasFailedRecords);

/// <summary>An import in full, as the detail screen renders it.</summary>
/// <param name="BatchId">Opaque identifier.</param>
/// <param name="FileName">Name of the uploaded file.</param>
/// <param name="FileSizeBytes">Size of the upload.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Statistics">Row counters.</param>
/// <param name="UploadedAt">Instant the file was accepted.</param>
/// <param name="CompletedAt">Instant it finished.</param>
/// <param name="UploadedBy">Display name of whoever uploaded it.</param>
/// <param name="HasFailedRecords">Whether a failed-record export can be produced.</param>
/// <param name="ProgressPercent">
/// Real progress, reported rather than inferred. The client draws this value directly.
/// </param>
/// <param name="DetectedColumns">
/// Headers found in the file, in file order. Empty while pending or processing; the client hides
/// the mapping until it fills.
/// </param>
/// <param name="SuggestedMapping">Best guess from the headers.</param>
/// <param name="Mapping">What the operator saved, or null. The client falls back to the suggestion.</param>
/// <param name="ErrorGroups">Failure counts by reason.</param>
/// <param name="FailureReason">Batch-level failure — an unreadable file, a worker giving up.</param>
/// <param name="PlanLimit">The ceiling, when one is relevant.</param>
public sealed record ImportBatchDetail(
    string BatchId,
    string FileName,
    long FileSizeBytes,
    BatchStatus Status,
    ImportStatistics Statistics,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CompletedAt,
    string UploadedBy,
    bool HasFailedRecords,
    int ProgressPercent,
    IReadOnlyList<string> DetectedColumns,
    ImportColumnMap SuggestedMapping,
    ImportColumnMap? Mapping,
    IReadOnlyList<ImportErrorGroup> ErrorGroups,
    string? FailureReason,
    ImportPlanLimit? PlanLimit);

/// <summary>One failure on one row.</summary>
/// <param name="Code">The reason.</param>
/// <param name="Field">Contact field it concerns, when it concerns one.</param>
/// <param name="Message">Fallback wording for a code the client does not recognise.</param>
public sealed record ImportRowErrorDetail(ImportErrorCode Code, string? Field, string Message);

/// <summary>One staged row.</summary>
/// <param name="RowNumber">Spreadsheet row number. The header is row 1.</param>
/// <param name="Status">What happened to it.</param>
/// <param name="Values">
/// Cells keyed by source column header, matching <c>detectedColumns</c>. Keyed rather than
/// positional so the client can render one table column per detected column without tracking order.
/// </param>
/// <param name="Errors">Why it could not be used.</param>
public sealed record ImportRowDetail(
    int RowNumber,
    RowStatus Status,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<ImportRowErrorDetail> Errors);

/// <summary>
/// Query parameters for the import history list.
/// <para>
/// <see cref="Status"/> accepts the literal <c>all</c>, matching every other list in this API, so
/// the client clears a filter the same way everywhere.
/// </para>
/// </summary>
public sealed class ImportBatchQuery : PageRequest
{
    /// <summary>Sentinel meaning "do not filter".</summary>
    public const string All = "all";

    /// <summary>Batch status to filter by, or <c>all</c>.</summary>
    public string Status { get; init; } = All;

    /// <summary>Only imports uploaded on or after this instant.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only imports uploaded on or before this instant.</summary>
    public DateTimeOffset? To { get; init; }
}

/// <summary>Query parameters for the row list.</summary>
public sealed class ImportRowFilter : PageRequest
{
    /// <summary>Sentinel meaning "do not filter".</summary>
    public const string All = "all";

    /// <summary>Row status to filter by, or <c>all</c>.</summary>
    public string Status { get; init; } = All;
}

/// <summary>Request to save a column mapping.</summary>
/// <param name="Mapping">All seven keys; null means unmapped.</param>
public sealed record SaveImportMappingRequest(ImportColumnMap Mapping);

/// <summary>Response to an accepted upload.</summary>
/// <param name="BatchId">Opaque identifier.</param>
/// <param name="FileName">Name of the uploaded file.</param>
/// <param name="FileSizeBytes">Size of the upload.</param>
/// <param name="Status">Always <c>Pending</c>; the parse has not started.</param>
/// <param name="UploadedAt">Instant it was accepted.</param>
public sealed record ImportUploadResponse(
    string BatchId,
    string FileName,
    long FileSizeBytes,
    BatchStatus Status,
    DateTimeOffset UploadedAt);

/// <summary>Response to an accepted commit.</summary>
/// <param name="BatchId">Opaque identifier.</param>
/// <param name="Status">Always <c>Committing</c>.</param>
/// <param name="QueuedAt">Instant the work was queued.</param>
public sealed record ImportCommitResponse(string BatchId, BatchStatus Status, DateTimeOffset QueuedAt);

/// <summary>A failed-record export job.</summary>
/// <param name="ExportId">Opaque identifier, prefixed <c>exp_</c>.</param>
/// <param name="BatchId">Import it covers.</param>
/// <param name="Status">Where generation has got to.</param>
/// <param name="FileName">Name the download is served under.</param>
/// <param name="RowCount">Rows in the workbook.</param>
/// <param name="RequestedAt">Instant it was asked for.</param>
/// <param name="CompletedAt">Instant it finished.</param>
/// <param name="FailureReason">Why generation failed.</param>
public sealed record ImportExportJob(
    string ExportId,
    string BatchId,
    ExportStatus Status,
    string FileName,
    int RowCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);

/// <summary>
/// The enum converters the import contract needs, for the host to register.
/// </summary>
/// <remarks>
/// The <c>[JsonConverter]</c> attributes on the enums themselves are not enough on their own. A
/// converter in <c>JsonSerializerOptions.Converters</c> beats a type-level attribute, and this API
/// registers a global camelCase enum converter — so without these, every import enum would quietly
/// serialise camelCase against a contract that specifies PascalCase. Registered <b>before</b> the
/// global converter, because the first match wins.
/// <para>
/// The attributes still earn their place: they cover serializers configured elsewhere, notably the
/// realtime hub, so <c>importProgress</c> reports the same literals as the REST endpoints.
/// </para>
/// </remarks>
public static class ImportContractJson
{
    /// <summary>Converters that keep the import enums PascalCase on the wire.</summary>
    public static IReadOnlyList<JsonConverter> Converters { get; } =
    [
        new JsonStringEnumConverter<BatchStatus>(),
        new JsonStringEnumConverter<RowStatus>(),
        new JsonStringEnumConverter<ExportStatus>(),
        new JsonStringEnumConverter<ImportErrorCode>(),
    ];
}
