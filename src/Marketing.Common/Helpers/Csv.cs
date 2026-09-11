using System.Globalization;
using System.Text;

namespace Marketing.Common.Helpers;

/// <summary>
/// Writes CSV that survives the programs people actually open it in.
/// </summary>
/// <remarks>
/// Every cell is quoted rather than only the ones that need it. Conditional quoting is where CSV
/// writers go wrong: the rule is easy to state and easy to get subtly wrong, and the failure shows
/// up as a shifted column in one row of a thousand, long after anyone is looking.
/// </remarks>
public static class Csv
{
    /// <summary>
    /// Byte order mark, written before the first row.
    /// </summary>
    /// <remarks>
    /// Excel reads a BOM-less file as the system's legacy code page, so accented names arrive
    /// mangled. This data is full of them.
    /// </remarks>
    public const string ByteOrderMark = "﻿";

    /// <summary>Renders one row, terminated by CRLF as RFC 4180 requires.</summary>
    /// <param name="cells">Cell values in column order. A null is written as an empty cell.</param>
    public static string Row(params string?[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var builder = new StringBuilder();

        for (var index = 0; index < cells.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Cell(cells[index]));
        }

        // CRLF, not LF. RFC 4180 specifies it, and the readers that care are the ones on Windows.
        return builder.Append("\r\n").ToString();
    }

    /// <summary>Quotes a single value.</summary>
    private static string Cell(string? value)
    {
        // A quote inside a quoted field is escaped by doubling it. Missing this is the classic CSV
        // injection of a stray column, because the reader treats the lone quote as the field's end.
        var escaped = (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Formats an instant for a spreadsheet.
    /// </summary>
    /// <remarks>
    /// Round-trip ISO 8601 with the offset, so the value is unambiguous and sorts lexically. A
    /// localised rendering would be read differently in London and Karachi, and this file is
    /// routinely mailed between the two.
    /// </remarks>
    public static string Instant(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Formats a number without a culture's thousands separators.</summary>
    /// <remarks>
    /// Invariant deliberately: a separator would split the value across two columns in any locale
    /// whose separator is a comma.
    /// </remarks>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
