using System.Globalization;
using AwesomeAssertions;
using Marketing.Common.Helpers;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Writing CSV that survives the programs people actually open it in.
/// </summary>
/// <remarks>
/// The failure log is the first export anyone opens in Excel, and its reason strings routinely
/// contain commas and quotes because they carry provider error text verbatim. A writer that gets
/// escaping subtly wrong shifts one row of a thousand into the wrong columns, which nobody notices
/// until they are counting failures that are not there.
/// </remarks>
public sealed class CsvTests
{
    [Fact]
    public void Every_cell_is_quoted_even_when_it_need_not_be()
    {
        // Unconditional rather than clever. Conditional quoting is a rule that is easy to state and
        // easy to get subtly wrong, and its mistakes surface far from where they are made.
        Csv.Row("a", "b").Should().Be("\"a\",\"b\"\r\n");
    }

    [Fact]
    public void A_comma_in_a_value_does_not_become_a_new_column()
    {
        // The everyday case: "Graph API returned 401. Code 190, trace ABC" is a single reason.
        var row = Csv.Row("Graph API returned 401. Code 190, trace ABC");

        row.Should().Be("\"Graph API returned 401. Code 190, trace ABC\"\r\n");
    }

    [Fact]
    public void A_quote_inside_a_value_is_doubled()
    {
        // A lone quote inside a quoted field ends the field as far as the reader is concerned, and
        // every remaining column on that row shifts left.
        Csv.Row("she said \"no\"").Should().Be("\"she said \"\"no\"\"\"\r\n");
    }

    [Fact]
    public void A_newline_inside_a_value_stays_inside_its_cell()
    {
        // Provider messages occasionally carry them. Quoted, a newline is part of the field;
        // unquoted it would silently truncate the export at that row.
        var row = Csv.Row("line one\nline two");

        row.Should().Be("\"line one\nline two\"\r\n");
    }

    [Fact]
    public void A_null_becomes_an_empty_cell_not_the_word_null()
    {
        // A contact with no name is blank, not "null" - which would otherwise be indistinguishable
        // from someone genuinely recorded that way.
        Csv.Row(null, "x").Should().Be("\"\",\"x\"\r\n");
    }

    [Fact]
    public void Rows_end_with_a_carriage_return_and_line_feed()
    {
        // RFC 4180 specifies CRLF, and the readers that care about it are the ones on Windows.
        Csv.Row("a").Should().EndWith("\r\n");
    }

    [Fact]
    public void The_byte_order_mark_is_the_one_excel_looks_for()
    {
        // Without it Excel reads the file as the system code page and mangles every accented name.
        Csv.ByteOrderMark.Should().Be("﻿");
    }

    [Fact]
    public void An_instant_is_written_unambiguously_and_sorts_lexically()
    {
        var value = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.FromHours(5));

        var written = Csv.Instant(value);

        // Round-trip ISO 8601, offset included. A localised rendering would be read differently in
        // London and Karachi, and this file is routinely mailed between the two.
        written.Should().Be("2026-09-08T14:30:00.0000000+05:00");
        DateTimeOffset.Parse(written, CultureInfo.InvariantCulture).Should().Be(value);
    }

    [Fact]
    public void A_number_carries_no_thousands_separator()
    {
        // In any locale whose separator is a comma, a separator would split the value across two
        // columns - the exact failure the quoting exists to prevent, reintroduced by formatting.
        Csv.Number(1234567).Should().Be("1234567");
    }

    [Fact]
    public void A_realistic_failure_row_round_trips_field_for_field()
    {
        // The shape the export actually writes, parsed back with a minimal RFC 4180 reader.
        var row = Csv.Row(
            "923367890092",
            "Ayesha \"Aish\" Khan",
            "Eid promo, batch 2",
            "Graph API returned 401. Code 190, trace XYZ",
            Csv.Number(190),
            Csv.Instant(new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero)));

        ParseSingleRow(row).Should().Equal(
            "923367890092",
            "Ayesha \"Aish\" Khan",
            "Eid promo, batch 2",
            "Graph API returned 401. Code 190, trace XYZ",
            "190",
            "2026-09-08T09:00:00.0000000+00:00");
    }

    /// <summary>Minimal RFC 4180 reader, so the test verifies the output rather than restating it.</summary>
    private static List<string> ParseSingleRow(string row)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < row.Length; index++)
        {
            var character = row[index];

            if (inQuotes)
            {
                if (character != '"')
                {
                    cell.Append(character);
                }
                else if (index + 1 < row.Length && row[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    break;
                default:
                    cell.Append(character);
                    break;
            }
        }

        return cells;
    }
}
