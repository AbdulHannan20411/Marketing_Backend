using AwesomeAssertions;
using Marketing.Application.Services.Email;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Deciding whether a failed email is retried, and when.
/// </summary>
/// <remarks>
/// The only part of the outbox with a decision in it. Getting the backoff wrong is how a transient
/// relay outage becomes a rate-limit ban; getting the ceiling wrong is how a permanently bad
/// address is retried on every poll for the life of the platform.
/// </remarks>
public sealed class OutboxRetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void The_wait_doubles_with_each_failure(int attemptCount, int expectedMinutes)
    {
        // Exponential from one minute. Retrying immediately would spend every remaining attempt
        // inside the same outage that caused the first failure.
        OutboxRetryPolicy.DelayFor(attemptCount)
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void The_first_failure_waits_a_minute_not_thirty_seconds()
    {
        // Pins the exponent's base case. An off-by-one here halves every delay in the sequence,
        // which is the sort of change that passes review and shows up as a ban.
        OutboxRetryPolicy.DelayFor(1).Should().Be(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_nonsensical_attempt_count_still_yields_the_base_delay(int attemptCount)
    {
        // Clamped rather than trusted. Zero would otherwise ask for half a minute, and a negative
        // number for a fraction of a second - a caller's bug turning into a retry storm.
        OutboxRetryPolicy.DelayFor(attemptCount).Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void An_absurd_attempt_count_does_not_produce_an_absurd_date()
    {
        // The other end of the clamp. Without it the exponent overflows into a delay measured in
        // centuries, and the row silently never sends again.
        OutboxRetryPolicy.DelayFor(int.MaxValue)
            .Should().Be(TimeSpan.FromMinutes(Math.Pow(2, OutboxRetryPolicy.MaximumAttempts - 1)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void A_message_is_abandoned_only_after_its_attempts_are_spent(int attemptCount, bool giveUp)
    {
        // The boundary is deliberate: the fifth failure is the last one, not the one after it.
        OutboxRetryPolicy.ShouldGiveUp(attemptCount).Should().Be(giveUp);
    }

    [Fact]
    public void The_whole_retry_sequence_fits_inside_a_quarter_of_an_hour()
    {
        // Documents what the constants add up to, so changing MaximumAttempts cannot quietly turn
        // a fifteen-minute wait into a multi-hour one that outlives the outage it was meant for.
        var total = Enumerable
            .Range(1, OutboxRetryPolicy.MaximumAttempts - 1)
            .Aggregate(TimeSpan.Zero, (sum, attempt) => sum + OutboxRetryPolicy.DelayFor(attempt));

        total.Should().Be(TimeSpan.FromMinutes(15));
    }
}
