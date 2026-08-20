using AwesomeAssertions;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Services.Campaigns;
using Marketing.Common.Exceptions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

public sealed class RecurrenceCalculatorTests
{
    private readonly RecurrenceCalculator _calculator = new();

    /// <summary>A rule with only the fields a given frequency actually reads.</summary>
    private static RecurrenceRule Rule(
        RecurrenceFrequency frequency,
        string startDate,
        string time = "09:00",
        string timeZone = "Europe/London",
        int interval = 1,
        int[]? weekdays = null,
        MonthlyMode? monthlyMode = null,
        int? dayOfMonth = null,
        MonthlyOrdinal? ordinal = null,
        int? ordinalWeekday = null,
        int? month = null,
        RecurrenceEndCondition endCondition = RecurrenceEndCondition.Never,
        string? endDate = null,
        int? occurrenceCount = null) =>
        new(
            frequency,
            interval,
            weekdays,
            monthlyMode,
            dayOfMonth,
            ordinal,
            ordinalWeekday,
            month,
            DateOnly.Parse(startDate),
            TimeOnly.Parse(time),
            timeZone,
            endCondition,
            endDate is null ? null : DateOnly.Parse(endDate),
            occurrenceCount);

    private static DateTimeOffset Utc(string instant) =>
        DateTimeOffset.Parse(instant).ToUniversalTime();

    [Fact]
    public void A_one_off_fires_once_and_never_again()
    {
        var rule = Rule(RecurrenceFrequency.Once, "2026-09-05");

        var first = _calculator.Next(rule, Utc("2026-09-01T00:00:00Z"));

        first.Should().Be(Utc("2026-09-05T08:00:00Z"));

        // 09:00 London in September is BST, so 08:00 UTC. Asking again from after it yields nothing.
        _calculator.Next(rule, first!.Value).Should().BeNull();
    }

    [Fact]
    public void A_daily_rule_steps_by_its_interval_from_the_start_date()
    {
        var rule = Rule(RecurrenceFrequency.Daily, "2026-09-05", interval: 3);

        var occurrences = _calculator.Between(rule, Utc("2026-09-01T00:00:00Z"), Utc("2026-09-15T00:00:00Z"));

        occurrences.Should().Equal(
            Utc("2026-09-05T08:00:00Z"),
            Utc("2026-09-08T08:00:00Z"),
            Utc("2026-09-11T08:00:00Z"),
            Utc("2026-09-14T08:00:00Z"));
    }

    [Fact]
    public void A_weekly_rule_expands_to_every_selected_weekday()
    {
        // Monday and Wednesday. 2026-09-05 is a Saturday, so the first hits are the 7th and 9th.
        var rule = Rule(RecurrenceFrequency.Weekly, "2026-09-05", weekdays: [1, 3]);

        var occurrences = _calculator.Between(rule, Utc("2026-09-01T00:00:00Z"), Utc("2026-09-17T00:00:00Z"));

        occurrences.Should().Equal(
            Utc("2026-09-07T08:00:00Z"),
            Utc("2026-09-09T08:00:00Z"),
            Utc("2026-09-14T08:00:00Z"),
            Utc("2026-09-16T08:00:00Z"));
    }

    [Fact]
    public void A_fortnightly_rule_anchors_on_the_week_of_the_start_date()
    {
        // Every two weeks on Monday, starting Saturday 5 September. The eligible weeks are the one
        // containing the start date and every second week after it; that first week's Monday is the
        // 31st of August, which is before the start date, so the series opens on the 14th.
        //
        // This is the RRULE convention, and the alternative - re-anchoring on the first matching
        // weekday - makes "every two weeks" mean different weeks depending on which day was picked.
        var rule = Rule(RecurrenceFrequency.Weekly, "2026-09-05", interval: 2, weekdays: [1]);

        var occurrences = _calculator.Between(rule, Utc("2026-09-01T00:00:00Z"), Utc("2026-09-30T00:00:00Z"));

        occurrences.Should().Equal(
            Utc("2026-09-14T08:00:00Z"),
            Utc("2026-09-28T08:00:00Z"));
    }

    [Theory]
    [InlineData("2026-01-31", "2026-02-28")]
    [InlineData("2026-03-31", "2026-04-30")]
    [InlineData("2028-01-31", "2028-02-29")]
    public void A_day_of_month_rule_clamps_to_the_last_day_of_a_short_month(string start, string expected)
    {
        // The client warns the operator that days 29 to 31 behave this way. Skipping the month
        // instead would silently drop a send the operator was told to expect.
        var rule = Rule(
            RecurrenceFrequency.Monthly,
            start,
            monthlyMode: MonthlyMode.DayOfMonth,
            dayOfMonth: 31);

        var next = _calculator.Next(rule, DateOnly.Parse(start).ToDateTime(new TimeOnly(23, 59)));

        DateOnly.FromDateTime(next!.Value.UtcDateTime).Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void The_last_weekday_of_a_month_is_the_final_match_not_the_fifth()
    {
        // October 2026 has five Fridays; November has four. "Last" must find both.
        var rule = Rule(
            RecurrenceFrequency.Monthly,
            "2026-10-01",
            monthlyMode: MonthlyMode.DayOfWeek,
            ordinal: MonthlyOrdinal.Last,
            ordinalWeekday: 5);

        var occurrences = _calculator.Between(rule, Utc("2026-09-30T00:00:00Z"), Utc("2026-12-01T00:00:00Z"));

        occurrences.Select(occurrence => DateOnly.FromDateTime(occurrence.UtcDateTime)).Should().Equal(
            DateOnly.Parse("2026-10-30"),
            DateOnly.Parse("2026-11-27"));
    }

    [Fact]
    public void An_ordinal_weekday_resolves_within_the_month()
    {
        // The third Wednesday of October 2026 is the 21st.
        var rule = Rule(
            RecurrenceFrequency.Monthly,
            "2026-10-01",
            monthlyMode: MonthlyMode.DayOfWeek,
            ordinal: MonthlyOrdinal.Third,
            ordinalWeekday: 3);

        var next = _calculator.Next(rule, Utc("2026-10-01T00:00:00Z"));

        DateOnly.FromDateTime(next!.Value.UtcDateTime).Should().Be(DateOnly.Parse("2026-10-21"));
    }

    [Fact]
    public void A_yearly_rule_fires_in_its_chosen_month()
    {
        var rule = Rule(
            RecurrenceFrequency.Yearly,
            "2026-01-15",
            monthlyMode: MonthlyMode.DayOfMonth,
            dayOfMonth: 15,
            month: 1);

        var occurrences = _calculator.Between(rule, Utc("2025-12-01T00:00:00Z"), Utc("2029-01-01T00:00:00Z"));

        occurrences.Select(occurrence => DateOnly.FromDateTime(occurrence.UtcDateTime)).Should().Equal(
            DateOnly.Parse("2026-01-15"),
            DateOnly.Parse("2027-01-15"),
            DateOnly.Parse("2028-01-15"));
    }

    [Fact]
    public void A_nine_am_campaign_stays_at_nine_am_across_the_spring_transition()
    {
        // This is the reason the rule stores local time and a zone rather than a UTC instant.
        // The clocks go forward on 29 March 2026: 09:00 is 09:00 UTC before and 08:00 UTC after.
        var rule = Rule(RecurrenceFrequency.Daily, "2026-03-27");

        var occurrences = _calculator.Between(rule, Utc("2026-03-26T00:00:00Z"), Utc("2026-03-31T00:00:00Z"));

        occurrences.Should().Equal(
            Utc("2026-03-27T09:00:00Z"),
            Utc("2026-03-28T09:00:00Z"),
            Utc("2026-03-29T08:00:00Z"),
            Utc("2026-03-30T08:00:00Z"));
    }

    [Fact]
    public void An_hour_that_does_not_exist_fires_at_the_start_of_the_next_valid_hour()
    {
        // 01:30 on 29 March 2026 never happens in London; the clock jumps 01:00 to 02:00. Skipping
        // would drop the send for the year, so it fires at 02:00 local, which is 01:00 UTC.
        var rule = Rule(RecurrenceFrequency.Daily, "2026-03-29", time: "01:30");

        var next = _calculator.Next(rule, Utc("2026-03-28T12:00:00Z"));

        next.Should().Be(Utc("2026-03-29T01:00:00Z"));
    }

    [Fact]
    public void An_hour_that_happens_twice_fires_on_the_first_pass()
    {
        // 01:30 on 25 October 2026 happens at BST and again an hour later at GMT. Taking the larger
        // offset picks the earlier pass: 00:30 UTC, not 01:30 UTC.
        var rule = Rule(RecurrenceFrequency.Daily, "2026-10-25", time: "01:30");

        var next = _calculator.Next(rule, Utc("2026-10-24T12:00:00Z"));

        next.Should().Be(Utc("2026-10-25T00:30:00Z"));
    }

    [Fact]
    public void An_end_date_stops_the_series()
    {
        var rule = Rule(
            RecurrenceFrequency.Daily,
            "2026-09-05",
            endCondition: RecurrenceEndCondition.OnDate,
            endDate: "2026-09-07");

        var occurrences = _calculator.Between(rule, Utc("2026-09-01T00:00:00Z"), Utc("2026-10-01T00:00:00Z"));

        occurrences.Should().HaveCount(3);
        _calculator.Next(rule, Utc("2026-09-07T12:00:00Z")).Should().BeNull();
    }

    [Fact]
    public void An_occurrence_count_stops_the_series()
    {
        var rule = Rule(
            RecurrenceFrequency.Daily,
            "2026-09-05",
            endCondition: RecurrenceEndCondition.AfterCount,
            occurrenceCount: 3);

        _calculator.Between(rule, Utc("2026-09-01T00:00:00Z"), Utc("2026-10-01T00:00:00Z"))
            .Should().HaveCount(3);

        // Counting is by firings already recorded, not by wall-clock position, so a campaign that
        // was paused and resumed resumes with the right number left.
        _calculator.Next(rule, Utc("2026-09-01T00:00:00Z"), occurrencesRun: 3).Should().BeNull();
    }

    [Fact]
    public void A_zone_the_server_cannot_resolve_is_refused_rather_than_defaulted()
    {
        var rule = Rule(RecurrenceFrequency.Daily, "2026-09-05", timeZone: "Mars/Olympus_Mons");

        var act = () => _calculator.Next(rule, Utc("2026-09-01T00:00:00Z"));

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void A_southern_hemisphere_zone_transitions_the_other_way()
    {
        // Sydney runs DST across the new year, so the same rule shifts in the opposite direction to
        // London. Clocks go forward on 4 October 2026, moving 09:00 local from UTC+10 to UTC+11:
        // the 3rd fires at 23:00 UTC the previous day, the 4th and 5th at 22:00.
        var rule = Rule(RecurrenceFrequency.Daily, "2026-10-03", timeZone: "Australia/Sydney");

        var occurrences = _calculator.Between(rule, Utc("2026-10-02T00:00:00Z"), Utc("2026-10-05T00:00:00Z"));

        occurrences.Should().Equal(
            Utc("2026-10-02T23:00:00Z"),
            Utc("2026-10-03T22:00:00Z"),
            Utc("2026-10-04T22:00:00Z"));
    }
}
