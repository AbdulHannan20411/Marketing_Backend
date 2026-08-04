using System.Text.Json;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marketing.Infrastructure.Redis;

/// <summary>
/// Redis-backed cache-aside implementation of <see cref="ICacheService"/>.
/// <para>
/// <b>Fail-open by design.</b> Every operation is wrapped so that a Redis outage produces a cache
/// miss, not an exception: reads fall through to PostgreSQL and writes are discarded. A cache is a
/// latency optimisation, and letting one take the API down with it converts a minor dependency
/// failure into a total outage.
/// </para>
/// <para>
/// After a failure the service stops attempting Redis for a cool-down window. Without that, every
/// request would serially pay the full connect timeout while the cache is unreachable, which is
/// slower than having no cache at all.
/// </para>
/// </summary>
public sealed class RedisCacheService : ICacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly RedisOptions _options;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<RedisCacheService> _logger;

    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="multiplexer">
    /// Shared connection, or null when the cache is disabled or could not be established at
    /// startup. Null is a supported state, not an error.
    /// </param>
    /// <param name="options">Cache settings.</param>
    /// <param name="dateTimeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public RedisCacheService(
        IConnectionMultiplexer? multiplexer,
        IOptions<RedisOptions> options,
        IDateTimeProvider dateTimeProvider,
        ILogger<RedisCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _multiplexer = multiplexer;
        _options = options.Value;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable =>
        _options.Enabled
        && _multiplexer?.IsConnected == true
        && _dateTimeProvider.UtcNow >= _circuitOpenUntil;

    /// <inheritdoc />
    public async Task<TValue?> GetAsync<TValue>(string key, CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            var payload = await GetDatabase().StringGetAsync(key);

            if (payload.IsNullOrEmpty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<TValue>(payload!, SerializerOptions);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            OpenCircuit(exception, nameof(GetAsync), key);
            return null;
        }
        catch (JsonException exception)
        {
            // A poisoned entry - usually a DTO whose shape changed without a key version bump.
            // Drop it rather than failing the request; the next read repopulates it.
            _logger.LogWarning(exception, "Discarding malformed cache entry {CacheKey}.", key);
            await RemoveAsync(key, cancellationToken);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<TValue>(
        string key,
        TValue value,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (!IsAvailable)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(value, SerializerOptions);
            var expiry = absoluteExpiration ?? TimeSpan.FromSeconds(_options.DefaultExpirationSeconds);

            await GetDatabase().StringSetAsync(key, payload, expiry);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            OpenCircuit(exception, nameof(SetAsync), key);
        }
    }

    /// <inheritdoc />
    public async Task<TValue?> GetOrCreateAsync<TValue>(
        string key,
        Func<CancellationToken, Task<TValue?>> factory,
        TimeSpan? absoluteExpiration = null,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await GetAsync<TValue>(key, cancellationToken);

        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(cancellationToken);

        if (value is null)
        {
            // Negative results are not cached. Doing so would need a sentinel and a separate,
            // shorter TTL; until a real cache-penetration problem shows up, that is complexity
            // without a demonstrated cause.
            return null;
        }

        await SetAsync(key, value, absoluteExpiration, cancellationToken);

        return value;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!IsAvailable)
        {
            return;
        }

        try
        {
            await GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            OpenCircuit(exception, nameof(RemoveAsync), key);
        }
    }

    /// <inheritdoc />
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (!IsAvailable || _multiplexer is null)
        {
            return;
        }

        try
        {
            var database = GetDatabase();

            foreach (var endpoint in _multiplexer.GetEndPoints())
            {
                var server = _multiplexer.GetServer(endpoint);

                if (server.IsReplica || !server.IsConnected)
                {
                    continue;
                }

                // SCAN, not KEYS: KEYS blocks the Redis event loop for the whole keyspace, which on
                // a shared instance stalls every other tenant's traffic. pageSize bounds each round
                // trip so a large tenant's purge stays incremental.
                await foreach (var key in server.KeysAsync(database.Database, $"{prefix}*", pageSize: 500)
                                   .WithCancellation(cancellationToken))
                {
                    await database.KeyDeleteAsync(key);
                }
            }
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            OpenCircuit(exception, nameof(RemoveByPrefixAsync), prefix);
        }
    }

    private IDatabase GetDatabase() =>
        _multiplexer?.GetDatabase()
        ?? throw new InvalidOperationException("Redis connection is not available.");

    private static bool IsCacheFailure(Exception exception) =>
        exception is RedisException
            or RedisTimeoutException
            or RedisConnectionException
            or TimeoutException
            or ObjectDisposedException
            or InvalidOperationException;

    private void OpenCircuit(Exception exception, string operation, string key)
    {
        _circuitOpenUntil = _dateTimeProvider.UtcNow.AddSeconds(_options.CircuitResetSeconds);

        _logger.LogWarning(
            exception,
            "Redis {Operation} failed for {CacheKey}; serving from the database and pausing cache access until {ResumeAt}.",
            operation,
            key,
            _circuitOpenUntil);
    }
}
