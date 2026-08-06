using Marketing.API.Realtime;
using Marketing.Application.Interfaces;
using Marketing.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Extensions;

/// <summary>Configures the real-time channel.</summary>
public static class RealtimeExtensions
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

        if (redis is { Enabled: true } && !string.IsNullOrWhiteSpace(redis.ConnectionString))
        {
            // Without a backplane, a push only reaches clients connected to the instance that
            // produced it - so with two replicas roughly half the users silently miss every
            // update. Redis is already a dependency, so this costs nothing extra to run.
            builder.AddStackExchangeRedis(redis.ConnectionString, options =>
                options.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal("marketing:signalr"));
        }

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

        return app;
    }

    /// <summary>Derives SignalR's user identifier from the token's subject claim.</summary>
    private sealed class ClaimsUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) =>
            connection.User?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
    }
}
