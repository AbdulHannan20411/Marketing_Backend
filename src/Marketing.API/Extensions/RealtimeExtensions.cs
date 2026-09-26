using Marketing.API.Realtime;
using Marketing.Application.Interfaces;
using Marketing.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Extensions;

/// <summary>Configures the real-time channel.</summary>
public static partial class RealtimeExtensions
{
    /// <summary>
    /// Registers SignalR, and a Redis backplane when one is configured.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddSignalR(options =>
        {
            // Surfaced to the client so a failed push shows a real reason instead of a generic
            // hub error. Safe here because the hub has no client-callable methods that could leak
            // internals through an exception message.
            options.EnableDetailedErrors = false;

            // Slightly under the default 30s client timeout, so the server is the side that
            // notices a dead connection first and cleans up its groups.
            options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
        });

        var redis = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>();

        // Three conditions, not two. UseSignalRBackplane is separate from Enabled because the
        // backplane is the one Redis dependency that is not fail-soft: it registers the
        // connection's subscription, so a connection it cannot register is refused rather than
        // served without updates. With Redis down that is every connection - the socket upgrades,
        // dies a couple of seconds later when the connect attempt times out, and the client
        // reconnects into the same wall for as long as the page is open.
        if (redis is { Enabled: true, UseSignalRBackplane: true }
            && !string.IsNullOrWhiteSpace(redis.ConnectionString))
        {
            // Without a backplane, a push only reaches clients connected to the instance that
            // produced it - so with two replicas roughly half the users silently miss every
            // update. Redis is already a dependency, so this costs nothing extra to run.
            builder.AddStackExchangeRedis(redis.ConnectionString, options =>
            {
                options.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal("marketing:signalr");

                // The backplane builds its own connection from the raw string, and its defaults are
                // wrong for a request path: it aborts on a failed connect and waits five seconds to
                // decide, three times over. With Redis down, every push paid that - inside the
                // request that caused it, which is how saving a campaign came to take forty seconds.
                //
                // These are the same settings RedisConnection uses for the cache: connect in the
                // background, fail the individual publish quickly, keep retrying so the backplane
                // starts working by itself once Redis returns.
                options.Configuration.AbortOnConnectFail = false;
                options.Configuration.ConnectTimeout = redis.OperationTimeoutMilliseconds;
                options.Configuration.SyncTimeout = redis.OperationTimeoutMilliseconds;
                options.Configuration.AsyncTimeout = redis.OperationTimeoutMilliseconds;
                options.Configuration.ConnectRetry = 1;
            });
        }

        // Pushes are queued by the notifier and sent by the background service, so no request ever
        // waits on the hub or its backplane.
        services.AddSingleton<IRealtimeDispatcher, RealtimeDispatcher>();
        services.AddHostedService<RealtimeDispatchService>();
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

        // Groups are keyed by the tenant and user claims, so the default claim-based user id
        // provider is not used; this keeps SignalR from inventing its own identity scheme.
        services.AddSingleton<IUserIdProvider, ClaimsUserIdProvider>();

        return services;
    }

    /// <summary>Maps the hub endpoint.</summary>
    /// <param name="app">Application.</param>
    public static WebApplication MapRealtime(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHub<RealtimeHub>(RealtimeHub.Route);

        // Said once, at startup, because the alternative is what actually happened: the only trace
        // of a backplane refusing every connection was three framework log lines in fourteen
        // hours, and the symptom that got noticed was a browser network tab.
        var redis = app.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>();

        if (redis is { Enabled: true, UseSignalRBackplane: true }
            && !string.IsNullOrWhiteSpace(redis.ConnectionString))
        {
            LogBackplaneOn(app.Logger, RealtimeHub.Route);
        }
        else
        {
            LogBackplaneOff(app.Logger, RealtimeHub.Route);
        }

        return app;
    }

    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Information,
        Message = "Realtime hub {Route} is using the Redis backplane. Every connection needs Redis: "
                  + "if Redis is unavailable the hub will refuse connections and clients will reconnect in a loop.")]
    private static partial void LogBackplaneOn(ILogger logger, string route);

    [LoggerMessage(
        EventId = 4111,
        Level = LogLevel.Information,
        Message = "Realtime hub {Route} is running without a backplane. Pushes reach clients on this "
                  + "instance only, which is correct for a single instance and wrong for more than one.")]
    private static partial void LogBackplaneOff(ILogger logger, string route);

    /// <summary>Derives SignalR's user identifier from the token's subject claim.</summary>
    private sealed class ClaimsUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) =>
            connection.User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
    }
}
