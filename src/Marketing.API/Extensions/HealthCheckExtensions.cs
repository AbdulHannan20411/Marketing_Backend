using HealthChecks.UI.Client;
using Marketing.Common.Constants;
using Marketing.DataAccess.Configurations;
using Marketing.Infrastructure.Redis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Marketing.API.Extensions;

/// <summary>Configures liveness and readiness probes.</summary>
public static class HealthCheckExtensions
{
    /// <summary>Tag marking a dependency the API cannot serve traffic without.</summary>
    private const string ReadinessTag = "ready";

    /// <summary>Registers the dependency health checks.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddApiHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>();
        var redisOptions = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>();

        var builder = services.AddHealthChecks();

        if (!string.IsNullOrWhiteSpace(databaseOptions?.ConnectionString))
        {
            // Tagged as a readiness dependency: without PostgreSQL the API can serve nothing, so
            // the orchestrator should stop routing traffic to this instance.
            builder.AddNpgSql(
                databaseOptions.ConnectionString,
                name: "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: [ReadinessTag]);
        }

        // ConnectionStrings:Redis is the override the options binding honours, so the probe has to
        // read it the same way or it will test an address the application is not using.
        var redisConnectionString = configuration.GetConnectionString("Redis") is { Length: > 0 } shared
            ? shared
            : redisOptions?.ConnectionString;

        if (redisOptions is { Enabled: true } && !string.IsNullOrWhiteSpace(redisConnectionString))
        {
            // Deliberately Degraded rather than Unhealthy, and deliberately not tagged for
            // readiness. The cache is fail-open, so losing it slows the API down; taking instances
            // out of rotation for it would turn a latency problem into an availability one.
            builder.AddRedis(
                redisConnectionString,
                name: "redis",
                failureStatus: HealthStatus.Degraded,
                tags: ["cache"]);
        }

        // The broker check is contributed by MassTransit itself, configured where the bus is
        // registered. It reports as "rabbitmq", degraded on failure and outside the readiness set.

        return services;
    }

    /// <summary>Maps the liveness and readiness endpoints.</summary>
    /// <param name="app">Application builder.</param>
    public static WebApplication MapApiHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness: is the process running? Runs no dependency check, so a database blip never
        // causes the orchestrator to restart an otherwise healthy container.
        app.MapHealthChecks("/health/live", new()
        {
            Predicate = _ => false,
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        }).AllowAnonymous();

        // Readiness: can this instance serve traffic?
        app.MapHealthChecks("/health/ready", new()
        {
            Predicate = registration => registration.Tags.Contains(ReadinessTag),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        }).AllowAnonymous();

        // Full detail, including degraded dependencies. Not anonymous - it enumerates the
        // platform's infrastructure.
        app.MapHealthChecks("/health", new()
        {
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        }).RequireAuthorization(AppConstants.Policies.SuperAdminOnly);

        return app;
    }
}
