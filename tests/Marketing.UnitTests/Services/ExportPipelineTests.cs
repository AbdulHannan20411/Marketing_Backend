using System.Text;
using AwesomeAssertions;
using Marketing.Application.Services.Exports;
using Marketing.DataAccess.Entities;
using Marketing.Infrastructure.Exports;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The export state machine.
/// </summary>
/// <remarks>
/// This is the idempotency mechanism, not a formality. A broker that may deliver the same message
/// twice and a cleanup job that runs against rows a worker might be holding are both races, and
/// both show up here as a move that should not have been allowed.
/// </remarks>
public sealed class ExportJobStateTests
{
    [Theory]
    [InlineData(ExportJobStatus.Queued, ExportJobStatus.Processing)]
    [InlineData(ExportJobStatus.Queued, ExportJobStatus.Cancelled)]
    [InlineData(ExportJobStatus.Processing, ExportJobStatus.Completed)]
    [InlineData(ExportJobStatus.Processing, ExportJobStatus.Failed)]
    [InlineData(ExportJobStatus.Processing, ExportJobStatus.Cancelled)]
    [InlineData(ExportJobStatus.Completed, ExportJobStatus.Expired)]
    [InlineData(ExportJobStatus.Failed, ExportJobStatus.Queued)]
    public void The_moves_the_lifecycle_needs_are_allowed(ExportJobStatus from, ExportJobStatus to)
    {
        ExportJobStates.CanMove(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExportJobStatus.Completed, ExportJobStatus.Processing)]
    [InlineData(ExportJobStatus.Completed, ExportJobStatus.Queued)]
    [InlineData(ExportJobStatus.Expired, ExportJobStatus.Completed)]
    [InlineData(ExportJobStatus.Cancelled, ExportJobStatus.Processing)]
    [InlineData(ExportJobStatus.Queued, ExportJobStatus.Completed)]
    public void The_moves_that_would_hide_a_race_are_not(ExportJobStatus from, ExportJobStatus to)
    {
        ExportJobStates.CanMove(from, to).Should().BeFalse();
    }

    [Fact]
    public void A_redelivered_message_cannot_restart_a_finished_export()
    {
        var job = new ExportJob
        {
            Dataset = "contacts",
            QueryJson = "{}",
            Fingerprint = "f",
            Status = ExportJobStatus.Completed,
        };

        var call = () => ExportJobStates.MoveTo(job, ExportJobStatus.Processing);

        // What stops a retry writing a second file and sending a second "your export is ready".
        call.Should().Throw<Marketing.Common.Exceptions.BusinessRuleException>();
        job.Status.Should().Be(ExportJobStatus.Completed);
    }

    [Fact]
    public void Moving_to_the_state_it_is_already_in_is_not_a_violation()
    {
        var job = new ExportJob
        {
            Dataset = "contacts",
            QueryJson = "{}",
            Fingerprint = "f",
            Status = ExportJobStatus.Processing,
        };

        var call = () => ExportJobStates.MoveTo(job, ExportJobStatus.Processing);

        // Setting a state to what it already is is what a retry does, and it is not an error.
        call.Should().NotThrow();
    }

    [Fact]
    public void A_stalled_worker_can_have_its_job_taken_back()
    {
        // Looks like going backwards and is not: the lease expired, and the alternative is an
        // export stuck at 40% for ever.
        ExportJobStates.CanMove(ExportJobStatus.Processing, ExportJobStatus.Queued).Should().BeTrue();
    }

    [Theory]
    [InlineData(ExportJobStatus.Completed, true)]
    [InlineData(ExportJobStatus.Failed, true)]
    [InlineData(ExportJobStatus.Cancelled, true)]
    [InlineData(ExportJobStatus.Expired, true)]
    [InlineData(ExportJobStatus.Queued, false)]
    [InlineData(ExportJobStatus.Processing, false)]
    public void Terminal_means_nothing_happens_without_help(ExportJobStatus status, bool terminal)
    {
        ExportJobStates.IsTerminal(status).Should().Be(terminal);
    }
}

/// <summary>
/// Writing the file.
/// </summary>
/// <remarks>
/// Mostly about batching. A progress callback that fired per row would be a database write and a
/// SignalR push per row, which on a million-row export is the dominant cost of the whole feature.
/// </remarks>
public sealed class ExportFileWriterTests
{
    /// <summary>Yields <paramref name="count"/> rows without building a list of them.</summary>
    private static async IAsyncEnumerable<object?[]> Rows(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return [$"cnt_{index}", $"Person {index}", index];
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_csv_export_starts_with_a_byte_order_mark_and_a_header()
    {
        using var destination = new MemoryStream();

        await new CsvExportFileWriter().WriteAsync(
            destination, ["ID", "Name", "Count"], Rows(2), (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(destination.ToArray());

        // Without the mark Excel reads UTF-8 as the system code page and mangles every
        // non-ASCII name in the file.
        text.Should().StartWith("﻿");
        text.Should().Contain("\"ID\",\"Name\",\"Count\"");
        text.Should().Contain("\"cnt_1\",\"Person 1\",\"1\"");
    }

    [Fact]
    public async Task Progress_is_reported_per_thousand_rows_and_not_per_row()
    {
        using var destination = new MemoryStream();

        var reports = new List<int>();

        var written = await new CsvExportFileWriter().WriteAsync(
            destination,
            ["ID", "Name", "Count"],
            Rows(5_000),
            (count, _) =>
            {
                reports.Add(count);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        written.Should().Be(5_000);

        // Five, not five thousand. Each one is a database write and a push.
        reports.Should().Equal(1_000, 2_000, 3_000, 4_000, 5_000);
    }

    [Fact]
    public async Task A_row_with_fewer_cells_than_columns_writes_blanks_rather_than_failing()
    {
        using var destination = new MemoryStream();

        async IAsyncEnumerable<object?[]> Short()
        {
            yield return ["only-one"];
            await Task.CompletedTask;
        }

        await new CsvExportFileWriter().WriteAsync(
            destination, ["A", "B", "C"], Short(), (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Encoding.UTF8.GetString(destination.ToArray()).Should().Contain("\"only-one\",\"\",\"\"");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(true, "true")]
    [InlineData(42, "42")]
    public void Cells_are_rendered_in_a_form_that_round_trips(object? value, string expected)
    {
        (CsvExportFileWriter.FormatCell(value) ?? string.Empty).Should().Be(expected);
    }

    [Fact]
    public void A_date_is_rendered_unambiguously_rather_than_in_a_local_format()
    {
        var value = new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

        // A local format would make the same export mean different days depending on who opened
        // it.
        CsvExportFileWriter.FormatCell(value).Should().StartWith("2026-09-27T09:00:00");
    }

    [Fact]
    public async Task An_excel_export_carries_the_headings_and_every_row()
    {
        using var destination = new MemoryStream();

        var written = await new XlsxExportFileWriter().WriteAsync(
            destination, ["ID", "Name", "Count"], Rows(25), (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        written.Should().Be(25);

        destination.Position = 0;

        using var workbook = new ClosedXML.Excel.XLWorkbook(destination);
        var sheet = workbook.Worksheets.First();

        sheet.Cell(1, 1).GetString().Should().Be("ID");
        sheet.Cell(2, 2).GetString().Should().Be("Person 1");
        sheet.Cell(26, 2).GetString().Should().Be("Person 25");

        // Typed, not stringified: the reason to choose XLSX over CSV is that a number sums.
        sheet.Cell(2, 3).GetDouble().Should().Be(1);
    }

    [Fact]
    public void The_two_writers_declare_different_formats_and_extensions()
    {
        var csv = new CsvExportFileWriter();
        var xlsx = new XlsxExportFileWriter();

        // How the runner picks one. Two writers claiming the same format would make that
        // selection arbitrary.
        csv.Format.Should().Be(ExportFormat.Csv);
        xlsx.Format.Should().Be(ExportFormat.Xlsx);
        csv.FileExtension.Should().Be(".csv");
        xlsx.FileExtension.Should().Be(".xlsx");
    }
}
