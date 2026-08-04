namespace Marketing.Shared.Abstractions;

/// <summary>
/// Distributed cache abstraction implementing the cache-aside pattern.
/// <para>
/// Every implementation in this solution is <em>fail-open</em>: if Redis is unreachable, reads
/// return a miss and writes are dropped, so the caller falls through to PostgreSQL and the request
/// still succeeds. A cache outage must degrade latency, never availability.
/// </para>
/// </summary>
public interface ICacheService
{
    /// <summary>Reads a value, returning <see langword="null"/> on a miss or on cache failure.</summary>
    /// <typeparam name="TValue">Value type; must be JSON-serialisable.</typeparam>
    /// <param name="key">Fully-qualified cache key, built via <c>CacheKeys</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TValue?> GetAsync<TValue>(string key, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>Writes a value, silently discarding it if the cache is unavailable.</summary>
    /// <param name="key">Fully-qualified cache key.</param>
    /// <param name="value">Value to store.</param>
    /// <param name="absoluteExpiration">Time to live. Defaults to the configured value when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync<TValue>(
        string key,
        TValue value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>
    /// Returns the cached value, or invokes <paramref name="factory"/> and caches its result.
    /// This is the method callers should reach for; it keeps the miss path in one place.
    /// </summary>
    /// <param name="key">Fully-qualified cache key.</param>
    /// <param name="factory">Loader invoked on a miss. Must be safe to call concurrently.</param>
    /// <param name="absoluteExpiration">Time to live.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TValue?> GetOrCreateAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue?>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>Removes a single key.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every key under a prefix. Used to purge a tenant's cache when its settings,
    /// templates or entitlements change.
    /// </summary>
    /// <param name="prefix">Key prefix, built via <c>CacheKeys.TenantPrefix</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Whether the backing store is currently reachable. Reported by the health check.</summary>
    bool IsAvailable { get; }
}
