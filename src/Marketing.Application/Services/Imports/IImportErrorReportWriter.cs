namespace Marketing.Application.Services.Imports;

/// <summary>One row that could not be used, as the failed-record report renders it.</summary>
/// <param name="RowNumber">Row number in the source file. The header is row 1.</param>
/// <param name="Values">The original cells, positionally aligned with the file's headers.</param>
/// <param name="ErrorCode">Machine-readable reason.</param>
/// <param name="Message">Wording for whoever opens the workbook.</param>
public sealed record ImportFailedRow(
    int RowNumber,
    IReadOnlyList<string> Values,
    string ErrorCode,
    string Message);

/// <summary>
/// Builds the workbook of rows an import could not use.
/// <para>
/// A seam, like the file readers: the spreadsheet library lives in Infrastructure, and the worker
/// that decides <em>what</em> goes in the report has no opinion about the format it is written in.
/// </para>
/// <para>
/// The report is deliberately the operator's correction file — the original columns, in the
/// original order, with the reason appended. They fix the cells the report names and upload the
/// same file back, rather than hunting failures inside the file they started with.
/// </para>
/// </summary>
public interface IImportErrorReportWriter
{
    /// <summary>Extension the written file carries, including the dot.</summary>
    public string FileExtension { get; }

    /// <summary>Media type the written file is served under.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Writes the report.
    /// </summary>
    /// <param name="destination">Stream to write to. The caller owns and disposes it.</param>
    /// <param name="columns">The source file's headers, in file order.</param>
    /// <param name="rows">The failed rows, streamed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were written.</returns>
    public Task<int> WriteAsync(
        Stream destination,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<ImportFailedRow> rows,
        CancellationToken cancellationToken = default);
}
