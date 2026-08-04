using System.Net;
using Marketing.Common.Exceptions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.Resilience;

/// <summary>Builds the resilience pipelines applied to outbound dependencies.</summary>
public static class ResiliencePipelineFactory
{
    /// <summary>
    /// Builds the pipeline used for non-HTTP outbound work.
    /// <para>
    /// Strategy order matters and reads outermost-first: total timeout, then bulkhead, then circuit
    /// breaker, then retry, then the per-attempt timeout. The per-attempt timeout has to be
    /// innermost so that it bounds each try rather than the whole sequence, and the total timeout
    /// outermost so that retries plus backoff can never exceed the caller's budget.
    /// </para>
    /// </summary>
    /// <param name="options">Resilience settings.</param>
    /// <param name="logger">Logger used to report state changes.</param>
    public static ResiliencePipeline Build(ResilienceOptions options, ILogger logger)
    {
        var builder = new ResiliencePipelineBuilder();
        Configure(builder, options, logger);

        return builder.Build();
    }

    /// <summary>
    /// Applies the resilience strategies to an existing builder, for use from the dependency
    /// injection registration where the container owns the builder.
    /// </summary>
    /// <param name="builder">Builder to configure.</param>
    /// <param name="options">Resilience settings.</param>
    /// <param name="logger">Logger used to report state changes.</param>
    public static ResiliencePipelineBuilder Configure(
        ResiliencePipelineBuilder builder,
        ResilienceOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        return builder
            .AddTimeout(TimeSpan.FromSeconds(options.TotalTimeoutSeconds))
            .AddConcurrencyLimiter(options.MaxConcurrentCalls)
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitBreakerFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreakerSamplingSeconds),
                MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerBreakSeconds),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                OnOpened = arguments =>
                {
                    logger.LogError(
                        arguments.Outcome.Exception,
                        "Circuit opened for {BreakDuration}; calls will fail fast until it closes.",
                        arguments.BreakDuration);

                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Circuit closed; outbound calls have resumed.");
                    return ValueTask.CompletedTask;
                },
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.RetryCount,
                Delay = TimeSpan.FromMilliseconds(options.BaseDelayMilliseconds),
                BackoffType = DelayBackoffType.Exponential,
                // Jitter: without it every caller that failed during the same outage retries in
                // lockstep and re-creates the thundering herd that caused it.
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
                OnRetry = arguments =>
                {
                    logger.LogWarning(
                        arguments.Outcome.Exception,
                        "Retrying outbound call. Attempt {AttemptNumber} after {RetryDelay}.",
                        arguments.AttemptNumber + 1,
                        arguments.RetryDelay);

                    return ValueTask.CompletedTask;
                },
            })
            .AddTimeout(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
    }

    /// <summary>
    /// Decides whether a failure is worth retrying.
    /// <para>
    /// Only transport faults, timeouts, 5xx responses and 429 qualify. A 400 or a 422 from Meta
    /// means the request itself is wrong, and retrying it wastes the tenant's rate-limit budget
    /// while never succeeding. A 401 is likewise a credential problem, not a blip.
    /// </para>
    /// </summary>
    private static bool IsTransient(Exception exception) =>
        exception switch
        {
            TimeoutRejectedException => true,
            TaskCanceledException => true,
            HttpRequestException httpRequest => IsTransientStatus(httpRequest.StatusCode),
            ExternalServiceException external => external.IsTransient,
            _ => false,
        };

    private static bool IsTransientStatus(HttpStatusCode? statusCode) =>
        statusCode switch
        {
            null => true, // No response at all: connection reset, DNS failure, socket timeout.
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            >= HttpStatusCode.InternalServerError => true,
            _ => false,
        };
}
