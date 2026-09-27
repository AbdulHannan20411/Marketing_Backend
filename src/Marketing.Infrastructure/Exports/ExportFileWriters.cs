using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Marketing.Application.Services.Exports;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Infrastructure.Exports;

/// <summary>
/// Writes an export as CSV, straight through to the destination.
/// </summary>
/// <remarks>
/// Reuses <see cref="Csv"/>, the same quoting and formatting the synchronous exports have always
/// used, so a file produced here is byte-identical in shape to one produced by the old endpoint.
/// </remarks>
public sealed class CsvExportFileWriter : IExportFileWriter
{
    /// <summary>
    /// Rows between progress reports.
    /// </summary>
    /// <remarks>
    /// Each report is a database write and a SignalR push. Per row that would be a million of
    /// each; per thousand it is a thousand, which is still enough for a progress bar to look
    /// continuous on any export big enough to watch.
    /// </remarks>
    private const int ProgressInterval = 1_000;

    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Csv;

    /// <inheritdoc />
    public string FileExtension => ".csv";

    /// <inheritdoc />
    public string ContentType => AppConstants.ContentTypes.Csv;

    /// <inheritdoc />
    public async Task<int> WriteAsync(
        Stream destination,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<object?[]> rows,
        Func<int, CancellationToken, Task> onProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(onProgress);

        // leaveOpen: the caller owns the stream and may still need to rewind it to upload.
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);

        // Written by hand so the mark appears exactly once, at the front. Without it Excel reads a
        // UTF-8 file as the system code page and mangles every non-ASCII name in the export.
        await writer.WriteAsync(Csv.ByteOrderMark);
        await writer.WriteAsync(Csv.Row([.. columns]));

        var written = 0;
        var cells = new string?[columns.Count];

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            for (var index = 0; index < cells.Length; index++)
            {
                cells[index] = index < row.Length ? FormatCell(row[index]) : null;
            }

            await writer.WriteAsync(Csv.Row(cells));

            written++;

            if (written % ProgressInterval == 0)
            {
                // Flushed with the report, so a reader of the part-written file and the progress
                // number are talking about the same amount of work.
                await writer.FlushAsync(cancellationToken);
                await onProgress(written, cancellationToken);
            }
        }

        await writer.FlushAsync(cancellationToken);

        return written;
    }

    /// <summary>
    /// Renders one cell, in a form that round-trips rather than one that reads nicely.
    /// </summary>
    /// <remarks>
    /// Public because it is the format contract of every CSV export and is worth asserting
    /// directly: a date rendered in a local format means different days to different readers,
    /// and that is not a thing to discover from a customer's spreadsheet.
    /// </remarks>
    /// <param name="value">Cell value.</param>
    public static string? FormatCell(object? value) => value switch
    {
        null => null,
        string text => text,

        // Round-trippable and unambiguous. A local format would make the same export mean
        // different days depending on who opened it.
        DateTimeOffset instant => Csv.Instant(instant),
        DateTime instant => instant.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

/// <summary>
/// Writes an export as an Excel workbook.
/// </summary>
/// <remarks>
/// ClosedXML, which the import error report already uses - no second spreadsheet library.
/// <para>
/// <b>The honest limitation:</b> ClosedXML builds the workbook in memory and writes it on save,
/// so this format's peak memory grows with the export. It is capped for that reason - see
/// <see cref="RowLimit"/> - and anything larger is refused at request time with a message telling
/// the user to choose CSV, which streams. Silently truncating, or letting a worker be killed
/// by the allocator, are the two worse options.
/// </para>
/// </remarks>
public sealed class XlsxExportFileWriter : IExportFileWriter
{
    /// <summary>
    /// Most rows this format will accept.
    /// </summary>
    /// <remarks>
    /// Below Excel's own 1,048,575 data-row ceiling, and low enough that the in-memory workbook
    /// stays in the tens of megabytes rather than the hundreds. CSV has no such limit.
    /// </remarks>
    public const int RowLimit = 200_000;

    /// <summary>Rows between progress reports.</summary>
    private const int ProgressInterval = 1_000;

    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Xlsx;

    /// <inheritdoc />
    public string FileExtension => ".xlsx";

    /// <inheritdoc />
    public string ContentType => AppConstants.ContentTypes.Xlsx;

    /// <inheritdoc />
    public async Task<int> WriteAsync(
        Stream destination,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<object?[]> rows,
        Func<int, CancellationToken, Task> onProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(onProgress);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Export");

        for (var index = 0; index < columns.Count; index++)
        {
            sheet.Cell(1, index + 1).Value = columns[index];
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var written = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var line = written + 2;

            for (var index = 0; index < columns.Count; index++)
            {
                Set(sheet.Cell(line, index + 1), index < row.Length ? row[index] : null);
            }

            written++;

            if (written % ProgressInterval == 0)
            {
                await onProgress(written, cancellationToken);
            }
        }

        workbook.SaveAs(destination);

        return written;
    }

    /// <summary>
    /// Writes one cell as the type it is.
    /// </summary>
    /// <remarks>
    /// Typed rather than stringified, because the point of choosing XLSX over CSV is that a date
    /// sorts as a date and a number sums. Strings are written as strings deliberately: a phone
    /// number with a leading <c>+</c> handed to Excel as a formula is the oldest bug in
    /// spreadsheet exports.
    /// </remarks>
    /// <param name="cell">Destination cell.</param>
    /// <param name="value">Value to write.</param>
    private static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string text:
                cell.SetValue(text);
                break;
            case bool flag:
                cell.SetValue(flag);
                break;
            case DateTimeOffset instant:
                cell.SetValue(instant.UtcDateTime);
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;
            case DateTime instant:
                cell.SetValue(instant);
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;
            case DateOnly date:
                cell.SetValue(date.ToDateTime(TimeOnly.MinValue));
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case int number:
                cell.SetValue(number);
                break;
            case long number:
                cell.SetValue(number);
                break;
            case decimal number:
                cell.SetValue(number);
                break;
            case double number:
                cell.SetValue(number);
                break;
            default:
                cell.SetValue(value.ToString());
                break;
        }
    }
}
