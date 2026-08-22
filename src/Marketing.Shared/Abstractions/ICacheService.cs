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
    public Task<TValue?> GetAsync<TValue>(
        string key,
        CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>Writes a value, silently discarding it if the cache is unavailable.</summary>
    /// <param name="key">Fully-qualified cache key.</param>
    /// <param name="value">Value to store.</param>
    /// <param name="absoluteExpiration">Time to live. Defaults to the configured value when null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetAsync<TValue>(
        string key,
        TValue value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>
    /// Returns the cached value, or invokes <paramref name="factory"/> and caches its result.
    /// This is the method callers should reach for; it keeps the miss path in one place.
    /// </summary>
    public Task<TValue?> GetOrCreateAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue?>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>
    /// Increments a counter and returns its new value, setting the expiry on first use.
    /// </summary>
    /// <remarks>
    /// A distinct operation rather than get-then-set, because a quota enforced by two round trips
    /// is not enforced at all: concurrent callers each read the same value and each write one more
    /// than it, so the limit is exceeded by however many requests arrive together.
    /// <para>
    /// Returns <see langword="null"/> when the cache is unavailable. A caller enforcing a limit must
    /// decide for itself whether an unavailable cache means allow or deny; this method will not
    /// choose on its behalf by returning a number it did not read.
    /// </para>
    /// </remarks>
    /// <param name="key">Fully-qualified cache key.</param>
    /// <param name="expiry">Time to live, applied only when the counter is created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<long?> IncrementAsync(
        string key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a single key.</summary>
    public Task RemoveAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every key under a prefix. Used to purge a tenant's cache when its settings,
    /// templates or entitlements change.
    /// </summary>
    public Task RemoveByPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the backing store is currently reachable. Reported by the health check.</summary>
    public bool IsAvailable { get; }
}
