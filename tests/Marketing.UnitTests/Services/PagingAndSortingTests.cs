using System.Linq.Expressions;
using AwesomeAssertions;
using Marketing.Business.Extensions;
using static Marketing.Common.Constants.AppConstants;
using Marketing.Common.Exceptions;
using Marketing.Common.Requests;
using Marketing.Common.Responses;

namespace Marketing.UnitTests.Services;

public sealed class PagingAndSortingTests
{
    private sealed record Row(string Name, int Score);

    private static readonly IReadOnlyDictionary<string, Expression<Func<Row, object?>>> AllowedSorts =
        new Dictionary<string, Expression<Func<Row, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = row => row.Name,
            ["score"] = row => row.Score,
        };

    private static readonly IQueryable<Row> Rows = new[]
    {
        new Row("Charlie", 30),
        new Row("Alice", 10),
        new Row("Bravo", 20),
    }.AsQueryable();

    [Fact]
    public void An_allowed_sort_key_is_applied()
    {
        var request = new PageRequest { SortBy = "name", SortDirection = SortDirection.Ascending };

        var result = Rows.ApplySort(request, AllowedSorts, row => row.Score).ToList();

        result.Select(row => row.Name).Should().ContainInOrder("Alice", "Bravo", "Charlie");
    }

    [Fact]
    public void Sort_keys_are_matched_case_insensitively()
    {
        var request = new PageRequest { SortBy = "SCORE", SortDirection = SortDirection.Descending };

        var result = Rows.ApplySort(request, AllowedSorts, row => row.Score).ToList();

        result.First().Score.Should().Be(30);
    }

    [Fact]
    public void An_unknown_sort_key_is_rejected_rather_than_ignored()
    {
        var request = new PageRequest { SortBy = "password_hash" };

        var act = () => Rows.ApplySort(request, AllowedSorts, row => row.Score).ToList();

        // The allow-list is the reason a client-supplied sort field can never reach the provider
        // as raw text. Silently ignoring an unknown key would hide the mistake instead.
        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey(nameof(PageRequest.SortBy));
    }

    [Theory]
    [InlineData(0, PageRequest.DefaultPageSize)]
    [InlineData(-5, PageRequest.DefaultPageSize)]
    [InlineData(50, 50)]
    [InlineData(10_000, PageRequest.MaxPageSize)]
    public void Page_size_is_clamped_rather_than_rejected(int requested, int expected)
    {
        new PageRequest { PageSize = requested }.PageSize.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(7, 7)]
    public void Page_number_is_coerced_to_at_least_one(int requested, int expected)
    {
        new PageRequest { PageNumber = requested }.PageNumber.Should().Be(expected);
    }

    [Fact]
    public void Skip_and_take_are_derived_from_the_clamped_values()
    {
        var request = new PageRequest { PageNumber = 3, PageSize = 20 };

        request.Skip.Should().Be(40);
        request.Take.Should().Be(20);
    }

    [Fact]
    public void Paged_result_reports_navigation_counters_correctly()
    {
        var page = new PagedResult<Row>([new Row("Alice", 10)], totalCount: 45, pageNumber: 2, pageSize: 20);

        page.TotalPages.Should().Be(3);
        page.HasPreviousPage.Should().BeTrue();
        page.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public void An_empty_page_reports_zero_pages_and_no_navigation()
    {
        var page = PagedResults.Empty<Row>(pageNumber: 1, pageSize: 25);

        page.TotalPages.Should().Be(0);
        page.HasPreviousPage.Should().BeFalse();
        page.HasNextPage.Should().BeFalse();
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public void Mapping_a_page_preserves_its_counters()
    {
        var page = new PagedResult<Row>([new Row("Alice", 10)], totalCount: 45, pageNumber: 2, pageSize: 20);

        var mapped = page.Map(row => row.Name);

        mapped.Items.Should().ContainSingle().Which.Should().Be("Alice");
        mapped.TotalCount.Should().Be(45);
        mapped.PageNumber.Should().Be(2);
        mapped.TotalPages.Should().Be(3);
    }
}
