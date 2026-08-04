using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Marketing.Infrastructure.Redis;

/// <summary>
/// Owns the shared Redis connection and its lifetime.
/// <para>
/// Connecting is deferred and failure-tolerant: if Redis is not reachable, <see cref="Multiplexer"/>
/// is null and the cache degrades to a permanent miss rather than the host refusing to start. A
/// cache the API cannot boot without is not a cache, it is a second database.
/// </para>
/// </summary>
public sealed class RedisConnection : IDisposable
{
    private readonly Lazy<IConnectionMultiplexer?> _multiplexer;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Cache settings.</param>
    /// <param name="logger">Logger.</param>
    public RedisConnection(IOptions<RedisOptions> options, ILogger<RedisConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;

        _multiplexer = new Lazy<IConnectionMultiplexer?>(
            () => Connect(settings, logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The shared connection, or null when Redis is disabled or unreachable.</summary>
    public IConnectionMultiplexer? Multiplexer => _multiplexer.Value;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_multiplexer.IsValueCreated)
        {
            _multiplexer.Value?.Dispose();
        }
    }

    private static IConnectionMultiplexer? Connect(RedisOptions options, ILogger logger)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Redis caching is disabled by configuration.");
            return null;
        }

        try
        {
            var configuration = ConfigurationOptions.Parse(options.ConnectionString);

            configuration.ConnectTimeout = options.OperationTimeoutMilliseconds;
            configuration.SyncTimeout = options.OperationTimeoutMilliseconds;
            configuration.AsyncTimeout = options.OperationTimeoutMilliseconds;
            configuration.ConnectRetry = 3;

            // The important one. Left at its default of true, a Redis instance that happens to be
            // down at startup poisons the multiplexer permanently - it never reconnects even after
            // Redis returns, so a brief cache outage becomes an outage until the next deploy.
            configuration.AbortOnConnectFail = false;

            return ConnectionMultiplexer.Connect(configuration);
        }
        catch (Exception exception) when (exception is RedisConnectionException or ArgumentException)
        {
            logger.LogWarning(
                exception,
                "Could not connect to Redis; the cache is unavailable and reads will fall through to PostgreSQL.");

            return null;
        }
    }
}
