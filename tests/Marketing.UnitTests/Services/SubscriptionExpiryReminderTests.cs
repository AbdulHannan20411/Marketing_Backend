using System.Globalization;
using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.Services.Billing;
using Marketing.DataAccess.Entities;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Covers the two decisions that make the reminder countdown correct: how many days are left, and
/// whether a reminder for that many days has already gone out.
/// </summary>
/// <remarks>
/// Exercised through reflection rather than by standing up the service. Both are private statics
/// with no dependencies, and the alternative - a fake repository, a fake mailer, a fake clock -
/// would test the wiring rather than the arithmetic, which is where the mistakes live.
/// </remarks>
public sealed class SubscriptionExpiryReminderTests
{
    private static readonly MethodInfo DaysUntilMethod =
        typeof(SubscriptionExpiryReminderService)
            .GetMethod("DaysUntil", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo IsDueMethod =
        typeof(SubscriptionExpiryReminderService)
            .GetMethod("IsDue", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static int DaysUntil(string expiresAt, string now) =>
        (int)DaysUntilMethod.Invoke(
            null,
            [
                DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(now, CultureInfo.InvariantCulture),
            ])!;

    private static bool IsDue(int? lastReminderDay, int daysRemaining) =>
        (bool)IsDueMethod.Invoke(
            null,
            [new TenantSubscription { LastExpiryReminderDay = lastReminderDay }, daysRemaining])!;

    [Theory]
    [InlineData("2026-09-08T09:00:00Z", "2026-09-01T09:00:00Z", 7)]
    [InlineData("2026-09-02T09:00:00Z", "2026-09-01T09:00:00Z", 1)]
    public void Days_remaining_counts_calendar_days(string expiresAt, string now, int expected)
    {
        DaysUntil(expiresAt, now).Should().Be(expected);
    }

    [Fact]
    public void A_subscription_expiring_late_tomorrow_has_one_day_left_not_two()
    {
        // Counted as calendar days, not elapsed hours. 23:00 tomorrow is 38 hours away, which
        // rounds to 1 whichever way you take it - but 01:00 tomorrow is 16 hours, and an elapsed
        // -hours calculation would call that 0 and skip the final warning entirely.
        DaysUntil("2026-09-02T23:00:00Z", "2026-09-01T09:00:00Z").Should().Be(1);
        DaysUntil("2026-09-02T01:00:00Z", "2026-09-01T09:00:00Z").Should().Be(1);
    }

    [Fact]
    public void The_first_reminder_is_due_seven_days_out()
    {
        IsDue(lastReminderDay: null, daysRemaining: 7).Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_due_before_the_seven_day_mark()
    {
        IsDue(lastReminderDay: null, daysRemaining: 8).Should().BeFalse();
        IsDue(lastReminderDay: null, daysRemaining: 30).Should().BeFalse();
    }

    [Fact]
    public void Every_day_from_seven_down_to_one_sends_exactly_one_reminder()
    {
        // The whole contract in one test: seven reminders, one per day, none repeated.
        int? marker = null;
        var sent = new List<int>();

        foreach (var day in Enumerable.Range(1, 7).Reverse())
        {
            if (IsDue(marker, day))
            {
                sent.Add(day);
                marker = day;
            }
        }

        sent.Should().Equal(7, 6, 5, 4, 3, 2, 1);
    }

    [Fact]
    public void Running_twice_in_the_same_day_sends_one_reminder()
    {
        // The job is scheduled daily but nothing guarantees it runs once: a restart, a manual
        // trigger, or two hosts both firing it. The second pass must be silent.
        IsDue(lastReminderDay: null, daysRemaining: 5).Should().BeTrue();
        IsDue(lastReminderDay: 5, daysRemaining: 5).Should().BeFalse();
    }

    [Fact]
    public void A_missed_day_still_produces_the_next_reminder()
    {
        // Down for a day, so the countdown jumps 5 to 3. The customer gets the 3-day warning
        // rather than nothing, because the test is "lower than last sent", not "exactly one less".
        IsDue(lastReminderDay: 5, daysRemaining: 3).Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_sent_once_the_subscription_has_expired()
    {
        // Zero and below are somebody else's problem - this service warns about what is coming,
        // and an expired plan needs a different message than "expires in 0 days".
        IsDue(lastReminderDay: 1, daysRemaining: 0).Should().BeFalse();
        IsDue(lastReminderDay: null, daysRemaining: -3).Should().BeFalse();
    }

    [Fact]
    public void A_renewed_subscription_starts_its_countdown_again()
    {
        // Renewal clears the marker. Without that the stored 1 would suppress every reminder for
        // the next period, since 7 is not lower than 1 - a subscription that warned nobody
        // because it had warned somebody once before.
        IsDue(lastReminderDay: 1, daysRemaining: 7).Should().BeFalse();
        IsDue(lastReminderDay: null, daysRemaining: 7).Should().BeTrue();
    }
}
