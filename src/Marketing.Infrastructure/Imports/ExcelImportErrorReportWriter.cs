using ClosedXML.Excel;
using Marketing.Application.Services.Imports;
using Marketing.Common.Constants;

namespace Marketing.Infrastructure.Imports;

/// <summary>
/// Writes the failed-record report as an Excel workbook.
/// <para>
/// XLSX rather than CSV because the report exists to be corrected by hand: the reason columns are
/// frozen alongside the operator's own data, and a spreadsheet keeps a leading <c>+</c> on a phone
/// number that a CSV opened in Excel would silently turn into a formula error.
/// </para>
/// </summary>
public sealed class ExcelImportErrorReportWriter : IImportErrorReportWriter
{
    /// <summary>Extra columns appended after the file's own, naming the failure.</summary>
    private static readonly string[] DiagnosticColumns = ["Row", "Error Code", "Error"];

    /// <inheritdoc />
    public string FileExtension => ".xlsx";

    /// <inheritdoc />
    public string ContentType => AppConstants.ContentTypes.Xlsx;

    /// <inheritdoc />
    public async Task<int> WriteAsync(
        Stream destination,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<ImportFailedRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Failed records");

        // The diagnostics go after the operator's columns, not before, so the file still opens
        // looking like the one they uploaded.
        for (var index = 0; index < columns.Count; index++)
        {
            sheet.Cell(1, index + 1).Value = columns[index];
        }

        for (var index = 0; index < DiagnosticColumns.Length; index++)
        {
            sheet.Cell(1, columns.Count + index + 1).Value = DiagnosticColumns[index];
        }

        var header = sheet.Row(1);

        header.Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var written = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var line = written + 2;

            for (var index = 0; index < columns.Count; index++)
            {
                // Set as text, so a number with a leading zero or plus survives the round trip
                // back into the next upload.
                sheet.Cell(line, index + 1).SetValue(
                    index < row.Values.Count ? row.Values[index] : string.Empty);
            }

            sheet.Cell(line, columns.Count + 1).Value = row.RowNumber;
            sheet.Cell(line, columns.Count + 2).Value = row.ErrorCode;
            sheet.Cell(line, columns.Count + 3).Value = row.Message;

            written++;
        }

        sheet.Columns().AdjustToContents(1, 200);

        workbook.SaveAs(destination);

        return written;
    }
}
