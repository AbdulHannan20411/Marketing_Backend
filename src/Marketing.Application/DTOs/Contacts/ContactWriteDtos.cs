using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Contacts;

/// <summary>Request to create a contact.</summary>
/// <param name="FullName">Full name.</param>
/// <param name="PhoneNumber">E.164 number.</param>
/// <param name="Email">Email address, when known.</param>
/// <param name="Country">ISO 3166-1 alpha-2 country code.</param>
/// <param name="Status">Marketing consent state.</param>
/// <param name="TagIds">Tags to apply.</param>
/// <param name="GroupIds">Groups to join.</param>
public sealed record CreateContactRequest(
    string FullName,
    string PhoneNumber,
    string? Email = null,
    string Country = "",
    ContactStatus Status = ContactStatus.Subscribed,
    IReadOnlyList<string>? TagIds = null,
    IReadOnlyList<string>? GroupIds = null);

/// <summary>
/// Request to update a contact.
/// <para>
/// Tag and group collections are replaced wholesale when supplied and left alone when omitted,
/// so the editor can save a name change without having to send the full membership back.
/// </para>
/// </summary>
public sealed record UpdateContactRequest(
    string? FullName = null,
    string? PhoneNumber = null,
    string? Email = null,
    string? Country = null,
    ContactStatus? Status = null,
    ContactLifecycle? Lifecycle = null,
    IReadOnlyList<string>? TagIds = null,
    IReadOnlyList<string>? GroupIds = null);

/// <summary>Request naming a set of contacts.</summary>
/// <param name="Ids">Contact identifiers.</param>
public sealed record BulkContactRequest(IReadOnlyList<string> Ids);

/// <summary>Request applying tags to a set of contacts.</summary>
/// <param name="Ids">Contact identifiers.</param>
/// <param name="TagIds">Tags to apply.</param>
public sealed record BulkTagRequest(IReadOnlyList<string> Ids, IReadOnlyList<string> TagIds);

/// <summary>Request adding a set of contacts to groups.</summary>
/// <param name="Ids">Contact identifiers.</param>
/// <param name="GroupIds">Groups to join.</param>
public sealed record BulkGroupRequest(IReadOnlyList<string> Ids, IReadOnlyList<string> GroupIds);

/// <summary>Outcome of a bulk operation.</summary>
/// <param name="Affected">Rows changed.</param>
/// <param name="Skipped">Rows that matched nothing, or were already in the requested state.</param>
public sealed record BulkOperationResult(int Affected, int Skipped);

/// <summary>
/// What the first import call returns: enough for the operator to map columns and decide whether
/// to commit.
/// </summary>
/// <param name="BatchId">Identifier to pass to the commit call.</param>
/// <param name="FileName">Name of the uploaded file.</param>
/// <param name="Columns">Detected column headers, in file order.</param>
/// <param name="SampleRows">First few parsed rows, positionally aligned with the columns.</param>
/// <param name="TotalRows">Data rows found.</param>
/// <param name="DuplicateRows">Rows whose phone number already exists in this tenant.</param>
/// <param name="InvalidRows">Rows that could not be parsed.</param>
/// <param name="SuggestedMapping">
/// Best-guess column mapping from the headers, so the wizard opens with fields already filled in
/// for the common case of a well-labelled export.
/// </param>
public sealed record ImportPreview(
    string BatchId,
    string FileName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    int TotalRows,
    int DuplicateRows,
    int InvalidRows,
    ImportColumnMapping SuggestedMapping);

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
/// <param name="Country">Column supplying the country code.</param>
public sealed record ImportColumnMapping(
    string? FullName = null,
    string? PhoneNumber = null,
    string? Email = null,
    string? Country = null);

/// <summary>Request to commit a staged import.</summary>
/// <param name="Mapping">Column mapping chosen by the operator.</param>
/// <param name="SkipDuplicates">
/// Whether to skip rows whose phone number already exists. Defaults to true; the alternative is
/// updating the existing contact, which is a destructive choice that should be deliberate.
/// </param>
/// <param name="TagIds">Tags to apply to every imported contact.</param>
/// <param name="GroupIds">Groups to add every imported contact to.</param>
public sealed record CommitImportRequest(
    ImportColumnMapping Mapping,
    bool SkipDuplicates = true,
    IReadOnlyList<string>? TagIds = null,
    IReadOnlyList<string>? GroupIds = null);

/// <summary>Outcome of a committed import.</summary>
/// <param name="Imported">Contacts created.</param>
/// <param name="SkippedDuplicates">Rows skipped because the number already existed.</param>
/// <param name="Failed">Rows that could not be imported.</param>
/// <param name="Errors">Per-row failures, capped so a broken file cannot return a huge payload.</param>
public sealed record ImportResult(
    int Imported,
    int SkippedDuplicates,
    int Failed,
    IReadOnlyList<ImportRowError> Errors);

/// <summary>One row that could not be imported.</summary>
/// <param name="RowNumber">One-based row number in the source file.</param>
/// <param name="Reason">Why it failed.</param>
public sealed record ImportRowError(int RowNumber, string Reason);
