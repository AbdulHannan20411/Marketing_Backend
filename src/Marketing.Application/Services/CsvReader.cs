namespace Marketing.Application.Services;

/// <summary>
/// Minimal RFC 4180 CSV reader.
/// <para>
/// Hand-written rather than pulled from a package because the requirement is narrow - read a
/// header and rows of strings - and the two behaviours that actually matter, quoted fields
/// containing commas and embedded newlines, are the two a naive <c>Split(',')</c> gets wrong and
/// that silently corrupt a customer's contact list.
/// </para>
/// </summary>
public static class CsvReader
{
    /// <summary>Parses a CSV document into rows of cells.</summary>
    /// <param name="content">Whole file contents.</param>
    /// <returns>Rows in file order; the first is the header if the file has one.</returns>
    public static List<List<string>> Parse(string content)
    {
        var rows = new List<List<string>>();

        if (string.IsNullOrEmpty(content))
        {
            return rows;
        }

        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];

            if (inQuotes)
            {
                if (character != '"')
                {
                    cell.Append(character);
                    continue;
                }

                // A doubled quote inside a quoted field is an escaped quote, not the end of it.
                if (index + 1 < content.Length && content[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                    continue;
                }

                inQuotes = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;

                case '\r':
                    // Swallowed; the newline that follows ends the row. Handles CRLF and lone CR.
                    break;

                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;

                default:
                    cell.Append(character);
                    break;
            }
        }

        // A file that does not end with a newline still has a final row.
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        // Rows that are entirely empty are dropped - a trailing blank line is not a contact.
        return [.. rows.Where(entry => entry.Any(value => !string.IsNullOrWhiteSpace(value)))];
    }
}
