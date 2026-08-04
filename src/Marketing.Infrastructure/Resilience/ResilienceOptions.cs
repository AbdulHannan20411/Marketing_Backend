using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.Resilience;

/// <summary>Outbound-call resilience settings, bound from the <c>Resilience</c> section.</summary>
public sealed class ResilienceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Resilience";

    /// <summary>Retry attempts made after the initial call.</summary>
    [Range(0, 10)]
    public int RetryCount { get; init; } = 3;

    /// <summary>Base delay for the exponential backoff.</summary>
    [Range(50, 10_000)]
    public int BaseDelayMilliseconds { get; init; } = 500;

    /// <summary>Per-attempt timeout.</summary>
    [Range(1, 300)]
    public int AttemptTimeoutSeconds { get; init; } = 10;

    /// <summary>Overall timeout across every attempt, including waits.</summary>
    [Range(1, 600)]
    public int TotalTimeoutSeconds { get; init; } = 60;

    /// <summary>Failure ratio within the sampling window that trips the circuit breaker.</summary>
    [Range(0.05, 1.0)]
    public double CircuitBreakerFailureRatio { get; init; } = 0.5;

    /// <summary>Sampling window used to evaluate the failure ratio.</summary>
    [Range(5, 600)]
    public int CircuitBreakerSamplingSeconds { get; init; } = 30;

    /// <summary>Minimum calls in the window before the breaker may trip.</summary>
    [Range(2, 1000)]
    public int CircuitBreakerMinimumThroughput { get; init; } = 10;

    /// <summary>How long the circuit stays open before a trial call is allowed.</summary>
    [Range(1, 600)]
    public int CircuitBreakerBreakSeconds { get; init; } = 30;

    /// <summary>
    /// Concurrent in-flight calls permitted to one dependency.
    /// <para>
    /// Bulkhead isolation: a downstream that has become slow rather than unavailable will otherwise
    /// absorb every request thread in the process, taking endpoints down that never touch it.
    /// </para>
    /// </summary>
    [Range(1, 1000)]
    public int MaxConcurrentCalls { get; init; } = 100;
}
