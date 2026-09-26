using AwesomeAssertions;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Requests;
using Marketing.DataAccess.Entities;
using NSubstitute;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The exported failure log arrives in the order the screen was showing.
/// </summary>
/// <remarks>
/// The screen can be re-sorted in a second; the CSV is the artefact that gets attached to an
/// email, and nothing in it says which order it is in. A file that disagrees with the table it was
/// exported from is therefore worse than the mismatch sounds.
/// </remarks>
public sealed class FailureExportSortTests
{
    private const long TenantId = 9101;
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly List<DeliveryFailure> _rows = [];
    private readonly IRepository<DeliveryFailure> _failures = Substitute.For<IRepository<DeliveryFailure>>();
    private readonly InMemoryQueryExecutor _queries = new();

    public FailureExportSortTests()
    {
        _failures.Query(Arg.Any<bool>()).Returns(_ => _rows.AsQueryable());
    }

    private AnalyticsService CreateService() =>
        new(
            Substitute.For<IRepository<MessageDailyStat>>(),
            Substitute.For<IRepository<ActivityEntry>>(),
            _failures,
            _queries,
            new FixedDateTimeProvider(Start));

    private void Failed(string campaign, string contact, int errorCode, int minutesAgo)
    {
        _rows.Add(new DeliveryFailure
        {
            Id = _rows.Count + 1,
            TenantId = TenantId,
            CampaignName = campaign,
            ContactName = contact,
            PhoneNumber = $"+9230000{_rows.Count + 1:0000}",
            Reason = $"Meta rejected the message ({errorCode}).",
            ErrorCode = errorCode,
            OccurredOn = Start.AddMinutes(-minutesAgo),
        });
    }

    private async Task<List<DeliveryFailureResponse>> ExportAsync(PageRequest request)
    {
        var exported = new List<DeliveryFailureResponse>();

        await foreach (var row in CreateService()
                           .StreamFailuresAsync(request, TestContext.Current.CancellationToken))
        {
            exported.Add(row);
        }

        return exported;
    }

    private static PageRequest SortedBy(string key, SortDirection direction = SortDirection.Ascending) =>
        new() { SortBy = key, SortDirection = direction };

    [Fact]
    public async Task The_export_follows_the_sort_it_was_given()
    {
        Failed("Eid Sale", "Ayesha Khan", errorCode: 131049, minutesAgo: 30);
        Failed("Ramadan", "Bilal Ahmed", errorCode: 131026, minutesAgo: 20);
        Failed("Eid Sale", "Sana Iqbal", errorCode: 131047, minutesAgo: 10);

        var exported = await ExportAsync(SortedBy("errorCode"));

        // The sequence that broke: group one Meta failure on screen, export it to send to
        // somebody, and the file arrives newest-first with those rows scattered through it.
        exported.Select(row => row.ErrorCode).Should().Equal(131026, 131047, 131049);
    }

    [Fact]
    public async Task The_export_and_the_screen_agree_for_the_same_sort()
    {
        Failed("Eid Sale", "Ayesha Khan", errorCode: 131049, minutesAgo: 30);
        Failed("Ramadan", "Bilal Ahmed", errorCode: 131026, minutesAgo: 20);
        Failed("Autumn", "Sana Iqbal", errorCode: 131047, minutesAgo: 10);

        var request = SortedBy("campaignName");

        var onScreen = await CreateService().GetFailuresAsync(request, TestContext.Current.CancellationToken);
        var exported = await ExportAsync(request);

        // Not a coincidence to be maintained: both read the one allow-list field, so this cannot
        // be made to fail without changing what a sort key means for both at once.
        exported.Select(row => row.Id).Should().Equal(onScreen.Items.Select(row => row.Id));
    }

    [Fact]
    public async Task Without_a_sort_the_export_is_still_newest_first()
    {
        Failed("Eid Sale", "Ayesha Khan", errorCode: 131049, minutesAgo: 30);
        Failed("Ramadan", "Bilal Ahmed", errorCode: 131026, minutesAgo: 10);
        Failed("Autumn", "Sana Iqbal", errorCode: 131047, minutesAgo: 20);

        var exported = await ExportAsync(new PageRequest());

        // The default is unchanged, so an export from an unsorted table produces the same file it
        // produced before this change.
        exported.Select(row => row.ContactName).Should().Equal("Bilal Ahmed", "Sana Iqbal", "Ayesha Khan");
    }

    [Fact]
    public async Task A_descending_sort_is_honoured_too()
    {
        Failed("Autumn", "Ayesha Khan", errorCode: 131049, minutesAgo: 30);
        Failed("Ramadan", "Bilal Ahmed", errorCode: 131026, minutesAgo: 20);

        var exported = await ExportAsync(SortedBy("campaignName", SortDirection.Descending));

        exported.Select(row => row.CampaignName).Should().Equal("Ramadan", "Autumn");
    }

    [Fact]
    public async Task Ties_are_settled_so_two_exports_of_the_same_log_match()
    {
        // One bad run supplies hundreds of rows with the same campaign and the same error code.
        Failed("Eid Sale", "Ayesha Khan", errorCode: 131049, minutesAgo: 30);
        Failed("Eid Sale", "Bilal Ahmed", errorCode: 131049, minutesAgo: 30);
        Failed("Eid Sale", "Sana Iqbal", errorCode: 131049, minutesAgo: 30);

        var first = await ExportAsync(SortedBy("campaignName"));
        var second = await ExportAsync(SortedBy("campaignName"));

        first.Select(row => row.Id).Should().Equal(second.Select(row => row.Id));
        first.Select(row => row.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Paging_on_the_request_is_ignored_and_the_whole_log_comes_out()
    {
        for (var index = 0; index < 30; index++)
        {
            Failed($"Campaign {index:00}", $"Contact {index:00}", errorCode: 131049, minutesAgo: index);
        }

        var exported = await ExportAsync(new PageRequest { Page = 2, PageSize = 5, SortBy = "campaignName" });

        // An export is the whole result set. A partial failure log that looks complete is a worse
        // artefact than none, which is why the page and page size are accepted and discarded
        // rather than refused - the client sends the list's request object unchanged.
        exported.Should().HaveCount(30);
        exported[0].CampaignName.Should().Be("Campaign 00");
    }

    [Fact]
    public async Task An_unsortable_field_is_refused_before_a_single_row_is_written()
    {
        Failed("Eid Sale", "Ayesha Khan", errorCode: 131049, minutesAgo: 10);

        var call = async () => await ExportAsync(SortedBy("phoneNumber; drop table"));

        // Composed before the response is touched, so this is a clean 422 rather than a truncated
        // CSV with an error page stapled to the end of it.
        (await call.Should().ThrowAsync<ValidationException>())
            .Which.Errors[nameof(PageRequest.SortBy)][0]
            .Should().Contain("not a sortable field");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("occurredAt")]
    [InlineData("campaignName")]
    [InlineData("contactName")]
    [InlineData("errorCode")]
    [InlineData("phoneNumber")]
    public async Task Every_key_the_list_accepts_the_export_accepts(string key)
    {
        Failed("Eid Sale", "Ayesha Khan", errorCode: 131049, minutesAgo: 10);

        // The client sends the download the same sortBy it sends the list. A key the list took and
        // the export refused would turn an ordinary export into a 422 nobody could explain.
        (await ExportAsync(SortedBy(key))).Should().ContainSingle();
    }
}
