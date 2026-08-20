using Marketing.Application.DTOs.Campaigns;
using Marketing.Common.Exceptions;
using NodaTime;
using NodaTime.TimeZones;
using static Marketing.Common.Constants.ContractEnums;
using MonthMode = Marketing.Common.Constants.ContractEnums.MonthlyMode;

namespace Marketing.Application.Services.Campaigns;

/// <summary>Computes when a recurring campaign fires next.</summary>
public interface IRecurrenceCalculator
{
    /// <summary>
    /// The first occurrence strictly after <paramref name="afterUtc"/>, or <see langword="null"/>
    /// when the rule has no further occurrences.
    /// </summary>
    /// <param name="rule">The recurrence rule.</param>
    /// <param name="afterUtc">Exclusive lower bound.</param>
    /// <param name="occurrencesRun">Firings already recorded, for an <c>afterCount</c> end condition.</param>
    public DateTimeOffset? Next(RecurrenceRule rule, DateTimeOffset afterUtc, int occurrencesRun = 0);

    /// <summary>Every occurrence in a window, oldest first. Used to detect missed firings.</summary>
    /// <param name="rule">The recurrence rule.</param>
    /// <param name="fromUtc">Exclusive lower bound.</param>
    /// <param name="toUtc">Inclusive upper bound.</param>
    /// <param name="occurrencesRun">Firings already recorded.</param>
    public IReadOnlyList<DateTimeOffset> Between(
        RecurrenceRule rule,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int occurrencesRun = 0);
}

/// <summary>
/// Computes occurrences in the campaign's own timezone, converting to UTC once at the end.
/// <para>
/// The order matters and is the whole point. Working in local time and converting per occurrence
/// keeps "every Monday at 9am" at 9am across a daylight-saving transition; converting once at save
/// time and adding intervals in UTC shifts it by an hour for half the year.
/// </para>
/// <para>
/// Occurrences are computed on demand rather than enumerated and stored. A rule that never ends has
/// no list to store, and a stored list is wrong the moment the rule is edited.
/// </para>
/// </summary>
public sealed class RecurrenceCalculator : IRecurrenceCalculator
{
    /// <summary>
    /// Ceiling on candidate dates examined for one query.
    /// <para>
    /// A rule can be satisfiable but distant, so the search cannot stop at the first miss. The cap
    /// keeps a malformed rule from spinning forever; it is far above any real schedule's reach.
    /// </para>
    /// </summary>
    private const int MaxCandidates = 2000;

    /// <summary>
    /// Decides the two local times a clock change makes unusable.
    /// <para>
    /// <b>The hour that happens twice.</b> On an autumn morning 01:30 occurs at both offsets.
    /// <see cref="Resolvers.ReturnEarlier"/> fires on the first pass, and the occurrence key stops
    /// the second.
    /// </para>
    /// <para>
    /// <b>The hour that does not exist.</b> On a spring-forward morning 01:30 never happens.
    /// <see cref="Resolvers.ReturnStartOfIntervalAfter"/> fires at the first valid instant after the
    /// gap rather than skipping the occurrence entirely.
    /// </para>
    /// </summary>
    private static readonly ZoneLocalMappingResolver ClockChangeResolver =
        Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnStartOfIntervalAfter);

    /// <inheritdoc />
    public DateTimeOffset? Next(RecurrenceRule rule, DateTimeOffset afterUtc, int occurrencesRun = 0)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var zone = ResolveZone(rule.TimeZone);

        if (HasFinished(rule, occurrencesRun))
        {
            return null;
        }

        var afterLocal = Instant.FromDateTimeOffset(afterUtc).InZone(zone).LocalDateTime.ToDateTimeUnspecified();

        foreach (var date in EnumerateDates(rule, DateOnly.FromDateTime(afterLocal.Date)))
        {
            if (IsPastEndDate(rule, date))
            {
                return null;
            }

            var local = date.ToDateTime(rule.Time);

            if (local <= afterLocal)
            {
                continue;
            }

            return ToUtc(local, zone);
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<DateTimeOffset> Between(
        RecurrenceRule rule,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int occurrencesRun = 0)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var found = new List<DateTimeOffset>();
        var cursor = fromUtc;
        var runs = occurrencesRun;

        while (found.Count < MaxCandidates
               && Next(rule, cursor, runs) is { } occurrence
               && occurrence <= toUtc)
        {
            found.Add(occurrence);
            cursor = occurrence;
            runs++;
        }

        return found;
    }

    /// <summary>Resolves an IANA zone, refusing clearly rather than defaulting to UTC.</summary>
    /// <remarks>
    /// <para>
    /// Defaulting would produce a campaign that fires at the wrong hour and reports success, which
    /// is worse than one that refuses to be scheduled.
    /// </para>
    /// <para>
    /// Read from NodaTime's bundled database rather than <see cref="TimeZoneInfo"/>. The solution
    /// builds with <c>InvariantGlobalization</c>, under which the BCL knows only UTC and every IANA
    /// name fails to resolve - on the server as well as in tests.
    /// </para>
    /// </remarks>
    private static DateTimeZone ResolveZone(string timeZone)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone);

        return zone ?? throw new ValidationException(
            "timeZone",
            $"The timezone \"{timeZone}\" is not recognised.");
    }

    private static bool HasFinished(RecurrenceRule rule, int occurrencesRun) =>
        rule.EndCondition == RecurrenceEndCondition.AfterCount
        && occurrencesRun >= (rule.OccurrenceCount ?? 0);

    private static bool IsPastEndDate(RecurrenceRule rule, DateOnly date) =>
        rule.EndCondition == RecurrenceEndCondition.OnDate
        && rule.EndDate is { } endDate
        && date > endDate;

    /// <summary>
    /// Converts a local wall-clock time to UTC, applying <see cref="ClockChangeResolver"/> to the
    /// two cases a daylight-saving transition creates.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime local, DateTimeZone zone) =>
        LocalDateTime.FromDateTime(local)
            .InZone(zone, ClockChangeResolver)
            .ToDateTimeOffset()
            .ToUniversalTime();

    /// <summary>Candidate local dates in ascending order, already anchored to the interval.</summary>
    private static IEnumerable<DateOnly> EnumerateDates(RecurrenceRule rule, DateOnly notBefore) =>
        rule.Frequency switch
        {
            RecurrenceFrequency.Once => [rule.StartDate],
            RecurrenceFrequency.Daily => Daily(rule, notBefore),
            RecurrenceFrequency.Weekly => Weekly(rule, notBefore),
            RecurrenceFrequency.Monthly => Monthly(rule, notBefore),
            RecurrenceFrequency.Yearly => Yearly(rule, notBefore),
            _ => [],
        };

    private static IEnumerable<DateOnly> Daily(RecurrenceRule rule, DateOnly notBefore)
    {
        var interval = Math.Max(1, rule.Interval);
        var elapsed = Math.Max(0, notBefore.DayNumber - rule.StartDate.DayNumber);
        var date = rule.StartDate.AddDays(StepsToReach(elapsed, interval) * interval);

        for (var index = 0; index < MaxCandidates; index++)
        {
            yield return date;

            date = date.AddDays(interval);
        }
    }

    private static IEnumerable<DateOnly> Weekly(RecurrenceRule rule, DateOnly notBefore)
    {
        var interval = Math.Max(1, rule.Interval);

        // Sunday-based weeks, matching the client's 0=Sunday numbering. Anchoring on the week
        // containing startDate is what makes "every two weeks" land on the intended weeks rather
        // than on whichever ones the epoch happens to make even.
        var anchor = StartOfWeek(rule.StartDate);
        var weeks = Math.Max(0, (StartOfWeek(notBefore).DayNumber - anchor.DayNumber) / 7);
        var week = anchor.AddDays(StepsToReach(weeks, interval) * interval * 7);

        var weekdays = (rule.Weekdays ?? [])
            .Where(day => day is >= 0 and <= 6)
            .Distinct()
            .Order()
            .ToArray();

        if (weekdays.Length == 0)
        {
            yield break;
        }

        for (var index = 0; index < MaxCandidates; index++)
        {
            foreach (var weekday in weekdays)
            {
                var date = week.AddDays(weekday);

                if (date >= rule.StartDate)
                {
                    yield return date;
                }
            }

            week = week.AddDays(interval * 7);
        }
    }

    private static IEnumerable<DateOnly> Monthly(RecurrenceRule rule, DateOnly notBefore)
    {
        var interval = Math.Max(1, rule.Interval);
        var first = new DateOnly(rule.StartDate.Year, rule.StartDate.Month, 1);
        var elapsed = Math.Max(0, ((notBefore.Year - first.Year) * 12) + notBefore.Month - first.Month);
        var steps = StepsToReach(elapsed, interval) * interval;

        for (var index = 0; index < MaxCandidates; index++, steps += interval)
        {
            var month = first.AddMonths(steps);

            if (ResolveDay(rule, month.Year, month.Month) is { } resolved && resolved >= rule.StartDate)
            {
                yield return resolved;
            }
        }
    }

    private static IEnumerable<DateOnly> Yearly(RecurrenceRule rule, DateOnly notBefore)
    {
        var interval = Math.Max(1, rule.Interval);
        var elapsed = Math.Max(0, notBefore.Year - rule.StartDate.Year);
        var steps = StepsToReach(elapsed, interval) * interval;

        for (var index = 0; index < MaxCandidates; index++, steps += interval)
        {
            var year = rule.StartDate.Year + steps;

            if (year > 9999)
            {
                yield break;
            }

            if (ResolveDay(rule, year, rule.Month ?? rule.StartDate.Month) is { } resolved
                && resolved >= rule.StartDate)
            {
                yield return resolved;
            }
        }
    }

    /// <summary>Picks the day within a month, by date or by ordinal weekday.</summary>
    private static DateOnly? ResolveDay(RecurrenceRule rule, int year, int month)
    {
        if (rule.MonthlyMode == MonthMode.DayOfWeek)
        {
            if (rule.OrdinalWeekday is not { } weekday || rule.Ordinal is not { } ordinal)
            {
                return null;
            }

            return OrdinalWeekdayOf(year, month, weekday, ordinal);
        }

        // Clamped, not skipped. The 31st of a 30-day month fires on the 30th - the client already
        // warns the operator that this is what days 29 to 31 do, and this honours that promise.
        var dayOfMonth = rule.DayOfMonth ?? rule.StartDate.Day;

        return new DateOnly(year, month, Math.Clamp(dayOfMonth, 1, DateTime.DaysInMonth(year, month)));
    }

    /// <summary>Resolves "the third Wednesday" or "the last Friday" within a month.</summary>
    private static DateOnly OrdinalWeekdayOf(int year, int month, int weekday, MonthlyOrdinal ordinal)
    {
        var days = DateTime.DaysInMonth(year, month);

        if (ordinal == MonthlyOrdinal.Last)
        {
            // From the end of the month backwards, so "last" is the final match whether that is the
            // fourth or the fifth. Counting forward to a fixed fifth would skip most months.
            var last = new DateOnly(year, month, days);

            return last.AddDays(-(((int)last.DayOfWeek - weekday + 7) % 7));
        }

        var first = new DateOnly(year, month, 1);
        var firstMatch = first.AddDays((weekday - (int)first.DayOfWeek + 7) % 7);
        var candidate = firstMatch.AddDays(7 * (int)ordinal);

        // A month with only four of a weekday has no fifth; fall back to the last match rather than
        // rolling into the following month, which would fire on the wrong date entirely.
        return candidate.Month == month
            ? candidate
            : firstMatch.AddDays(7 * ((days - firstMatch.Day) / 7));
    }

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    /// <summary>Whole intervals needed to reach or pass <paramref name="elapsed"/>.</summary>
    private static int StepsToReach(int elapsed, int interval) =>
        elapsed <= 0 ? 0 : (elapsed + interval - 1) / interval;
}
