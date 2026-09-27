using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Exports;

/// <summary>
/// What the client asks for when it clicks Export.
/// </summary>
/// <remarks>
/// Deliberately the list view's own state and nothing else. There is no row count, no page and no
/// identifier list: an export is "everything matching what I am looking at", and the server
/// resolves that itself rather than trusting a client to say how much there is.
/// </remarks>
/// <param name="Dataset">Which list view, by registry key - <c>contacts</c>, <c>failures</c>.</param>
/// <param name="Format">File format. Defaults to CSV.</param>
/// <param name="Search">The view's search box.</param>
/// <param name="Filters">The view's filters, by name. Unknown names are ignored, not refused.</param>
/// <param name="SortBy">Sort key, from the list endpoint's allow-list.</param>
/// <param name="SortDirection"><c>asc</c> or <c>desc</c>.</param>
/// <param name="Columns">Columns to write, in order. Empty for the dataset's defaults.</param>
public sealed record CreateExportRequest(
    string? Dataset,
    ExportFormat? Format,
    string? Search,
    IReadOnlyDictionary<string, string>? Filters,
    string? SortBy,
    string? SortDirection,
    IReadOnlyList<string>? Columns);

/// <summary>
/// An export job, as every export screen renders it.
/// </summary>
/// <param name="Id">Opaque identifier, prefixed <c>exj_</c>.</param>
/// <param name="Dataset">Registry key of the list view.</param>
/// <param name="DatasetName">What to call it on screen.</param>
/// <param name="Format">File format.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="TotalRecords">Rows it will contain, or null when the count was not worth taking.</param>
/// <param name="ProcessedRecords">Rows written so far.</param>
/// <param name="Percentage">
/// Completion, or null for indeterminate. Null whenever the total is unknown, so the client
/// renders a spinner rather than a bar that never moves.
/// </param>
/// <param name="FileName">Name the download will be saved as, once there is a file.</param>
/// <param name="FileSizeBytes">Size of the file, once there is one.</param>
/// <param name="ErrorMessage">Why it failed, in words a user can read. Never technical detail.</param>
/// <param name="RequestedAt">When it was asked for.</param>
/// <param name="CompletedAt">When it finished, either way.</param>
/// <param name="ExpiresAt">When the file stops being downloadable.</param>
/// <param name="IsDownloadable">
/// Whether a download would succeed right now. Computed server-side so the client does not have
/// to know the rules about status, file presence and expiry, and cannot get them wrong.
/// </param>
public sealed record ExportJobResponse(
    string Id,
    string Dataset,
    string DatasetName,
    ExportFormat Format,
    ExportJobStatus Status,
    int? TotalRecords,
    int ProcessedRecords,
    int? Percentage,
    string? FileName,
    long? FileSizeBytes,
    string? ErrorMessage,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt,
    bool IsDownloadable);

/// <summary>
/// What the API answers as soon as an export is accepted.
/// </summary>
/// <remarks>
/// Returned before any data is read, which is the point of the whole design: this response is
/// three inserts and a publish, and it is the same whether the export is of ten rows or a million.
/// </remarks>
/// <param name="JobId">Opaque identifier, prefixed <c>exj_</c>.</param>
/// <param name="Status">Always <c>queued</c>, unless an identical export was already running.</param>
/// <param name="Reused">
/// True when this is an export that was already in flight rather than a new one. The client says
/// "that export is already running" instead of implying a second file is coming.
/// </param>
public sealed record ExportAcceptedResponse(string JobId, ExportJobStatus Status, bool Reused);

/// <summary>One column a dataset can export, as the column picker renders it.</summary>
/// <param name="Key">Wire name to send back in <c>columns</c>.</param>
/// <param name="Heading">Label, and the heading written into the file.</param>
/// <param name="Default">Whether it is on when nothing is chosen.</param>
public sealed record ExportColumnResponse(string Key, string Heading, bool Default);

/// <summary>A list view that can be exported, as the export dialog renders it.</summary>
/// <param name="Key">Registry key to send as <c>dataset</c>.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Columns">Every column it can write.</param>
public sealed record ExportDatasetResponse(
    string Key,
    string Name,
    IReadOnlyList<ExportColumnResponse> Columns);

/// <summary>A file on its way to the browser.</summary>
/// <param name="Content">Readable stream. The caller disposes it.</param>
/// <param name="ContentType">Media type.</param>
/// <param name="FileName">Name to save as.</param>
public sealed record ExportDownload(Stream Content, string ContentType, string FileName);
