using System.Runtime.CompilerServices;
using System.Text;
using ClosedXML.Excel;
using Marketing.Application.Services.Imports;
using Marketing.Common.Exceptions;

namespace Marketing.Infrastructure.Imports;

/// <summary>
/// Reads CSV uploads a line at a time.
/// <para>
/// Streams rather than reading the file into a string, so memory stays flat regardless of size.
/// Handles quoted fields, embedded commas, doubled quotes and newlines inside a quoted cell —
/// all of which a naive <c>Split(',')</c> gets wrong on the first spreadsheet export it meets.
/// </para>
/// </summary>
public sealed class CsvImportFileReader : IImportFileReader
{
    /// <inheritdoc />
    public bool Handles(string extension) =>
        string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadHeaderAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = CreateReader(content);

        var header = await ReadRecordAsync(reader, cancellationToken);

        return header ?? throw new ValidationException("file", "The file is empty.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<string>> ReadRowsAsync(
        Stream content,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = CreateReader(content);

        // The header is consumed and discarded; the caller already has it.
        _ = await ReadRecordAsync(reader, cancellationToken);

        while (await ReadRecordAsync(reader, cancellationToken) is { } record)
        {
            yield return record;
        }
    }

    /// <summary>
    /// Opens the stream as UTF-8, detecting and stripping a byte order mark.
    /// </summary>
    /// <remarks>
    /// Spreadsheet exports routinely carry a BOM. Left in place it becomes part of the first
    /// column's name, and every mapping against that column silently fails to match.
    /// </remarks>
    private static StreamReader CreateReader(Stream content)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        return new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    }

    /// <summary>Reads one logical record, which may span several physical lines.</summary>
    private static async Task<List<string>?> ReadRecordAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.Peek() < 0)
        {
            return null;
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        while (true)
        {
            var next = reader.Read();

            if (next < 0)
            {
                break;
            }

            var character = (char)next;

            if (quoted)
            {
                if (character == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote, not the end of it.
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');

                        continue;
                    }

                    quoted = false;

                    continue;
                }

                field.Append(character);

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    fields.Add(field.ToString().Trim());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    fields.Add(field.ToString().Trim());

                    return fields;

                default:
                    field.Append(character);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        fields.Add(field.ToString().Trim());

        // A trailing newline produces one empty field, which is not a row.
        return fields.Count == 1 && fields[0].Length == 0 ? null : fields;
    }
}

/// <summary>
/// Reads XLSX uploads.
/// <para>
/// ClosedXML materialises the workbook, so the row cap on the upload is what bounds memory here.
/// A file large enough to matter should be uploaded as CSV, which streams.
/// </para>
/// </summary>
public sealed class ExcelImportFileReader : IImportFileReader
{
    /// <inheritdoc />
    public bool Handles(string extension) =>
        string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ReadHeaderAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        using var workbook = Open(content);

        var row = Sheet(workbook).FirstRowUsed()
                  ?? throw new ValidationException("file", "The file is empty.");

        return Task.FromResult(Cells(row));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<string>> ReadRowsAsync(
        Stream content,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var workbook = Open(content);

        var rows = Sheet(workbook).RowsUsed().Skip(1);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = Cells(row);

            // A row of nothing but empty cells is what trailing formatting leaves behind. It is
            // not a record and must not be counted as an invalid one.
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            yield return cells;
        }

        await Task.CompletedTask;
    }

    private static XLWorkbook Open(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        try
        {
            return new XLWorkbook(content);
        }
        catch (Exception exception)
        {
            throw new ValidationException(
                "file",
                "That file could not be read as a spreadsheet. Save it as .xlsx or .csv and try again.")
            {
                Data = { ["inner"] = exception.Message },
            };
        }
    }

    private static IXLWorksheet Sheet(XLWorkbook workbook) =>
        workbook.Worksheets.FirstOrDefault()
        ?? throw new ValidationException("file", "The workbook has no sheets.");

    /// <summary>Reads a row's cells as text, padded to the row's last used column.</summary>
    private static IReadOnlyList<string> Cells(IXLRangeRow row) =>
        [.. row.Cells(1, row.LastCellUsed()?.Address.ColumnNumber ?? 0)
            .Select(cell => cell.GetFormattedString().Trim())];

    /// <summary>Reads a worksheet row's cells as text.</summary>
    private static IReadOnlyList<string> Cells(IXLRow row) =>
        [.. row.Cells(1, row.LastCellUsed()?.Address.ColumnNumber ?? 0)
            .Select(cell => cell.GetFormattedString().Trim())];
}

/// <inheritdoc cref="IImportFileReaderFactory" />
public sealed class ImportFileReaderFactory : IImportFileReaderFactory
{
    private readonly IReadOnlyList<IImportFileReader> _readers;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="readers">Every registered reader.</param>
    public ImportFileReaderFactory(IEnumerable<IImportFileReader> readers)
    {
        _readers = [.. readers];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => [".csv", ".xlsx"];

    /// <inheritdoc />
    public IImportFileReader For(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        return _readers.FirstOrDefault(reader => reader.Handles(extension))
               ?? throw new ValidationException(
                   "file",
                   $"Upload a {string.Join(" or ", SupportedExtensions)} file.");
    }
}
