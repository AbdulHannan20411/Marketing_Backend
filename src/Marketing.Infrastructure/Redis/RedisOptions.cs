using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.Redis;

/// <summary>Cache settings, bound from the <c>Redis</c> configuration section.</summary>
public sealed class RedisOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Redis";

    /// <summary>
    /// StackExchange.Redis connection string.
    /// <para>
    /// Settable rather than init-only because <c>ConnectionStrings:Redis</c> overrides it after
    /// binding — see the post-configure step in the infrastructure registration.
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether the cache is enabled at all. Turning it off makes every read a miss, which is the
    /// quickest way to isolate a suspected stale-cache incident without a redeploy.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether SignalR's Redis backplane is registered.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Enabled"/>, because the two fail very differently. The cache is
    /// fail-soft: with Redis down a read is a miss and the request still answers. The backplane is
    /// not, and cannot be - it holds the connection's subscription, so a connection it cannot
    /// register is a connection that will silently miss every message. It therefore refuses the
    /// connection outright, and with Redis down that is every connection, forever, with the client
    /// reconnecting in between.
    /// <para>
    /// A backplane only earns that risk when there is more than one instance to bridge. Turn it
    /// off for a single-instance deployment and for development, where it buys nothing and takes
    /// the whole realtime channel with it whenever Redis is unavailable.
    /// </para>
    /// </remarks>
    public bool UseSignalRBackplane { get; init; } = true;

    /// <summary>Default time to live applied when a caller does not specify one.</summary>
    [Range(1, 86_400)]
    public int DefaultExpirationSeconds { get; init; } = 300;

    /// <summary>Connection and command timeout.</summary>
    [Range(100, 30_000)]
    public int OperationTimeoutMilliseconds { get; init; } = 2_000;

    /// <summary>
    /// How long to stop attempting Redis after a failure, before probing again. Prevents every
    /// request paying the connect timeout while the cache is down.
    /// </summary>
    [Range(1, 300)]
    public int CircuitResetSeconds { get; init; } = 30;
}
