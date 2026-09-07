namespace Marketing.Application.Services.Email;

/// <summary>
/// Decides whether a failed email is retried, and how long to wait.
/// </summary>
/// <remarks>
/// Separated from the processor because it is the only part with a decision in it. The processor
/// reads rows and calls a relay - neither is worth a test - while getting the backoff wrong is how
/// a transient outage becomes a rate-limit ban, and getting the ceiling wrong is how a permanently
/// bad address is retried on every poll for the life of the platform.
/// </remarks>
public static class OutboxRetryPolicy
{
    /// <summary>
    /// Attempts before a message is written off.
    /// <para>
    /// Five, spread over the delays below, covers a little over fifteen minutes - longer than any
    /// relay outage worth waiting through inside a product rather than fixing.
    /// </para>
    /// </summary>
    public const int MaximumAttempts = 5;

    /// <summary>Whether a message that has just failed should be abandoned.</summary>
    /// <param name="attemptCount">Attempts made so far, including the one that just failed.</param>
    public static bool ShouldGiveUp(int attemptCount) => attemptCount >= MaximumAttempts;

    /// <summary>
    /// How long to wait before the next attempt.
    /// </summary>
    /// <remarks>
    /// Exponential from one minute: 1, 2, 4, 8. A relay refusing mail now is usually still refusing
    /// it a second later, so retrying immediately spends the message's remaining attempts inside
    /// the same outage that caused the first failure.
    /// </remarks>
    /// <param name="attemptCount">Attempts made so far, including the one that just failed.</param>
    public static TimeSpan DelayFor(int attemptCount)
    {
        // Clamped before the subtraction, not after. Subtracting first overflows on int.MinValue
        // and wraps to int.MaxValue, which the clamp then reads as "the most failures possible" -
        // so the worst input produced the longest delay instead of the shortest.
        var exponent = Math.Clamp(attemptCount, 1, MaximumAttempts) - 1;

        return TimeSpan.FromMinutes(Math.Pow(2, exponent));
    }
}
