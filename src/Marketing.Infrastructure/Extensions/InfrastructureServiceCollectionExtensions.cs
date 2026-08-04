using Marketing.Infrastructure.Authentication;
using Marketing.Infrastructure.Logging;
using Marketing.Infrastructure.Quartz;
using Marketing.Infrastructure.Redis;
using Marketing.Infrastructure.Resilience;
using Marketing.Infrastructure.Time;
using Marketing.Infrastructure.WhatsApp;
using Marketing.Infrastructure.WhatsApp.Clients;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace Marketing.Infrastructure.Extensions;

/// <summary>Registers everything that talks to a dependency outside the process.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers ambient context, security primitives, cache, resilience, HTTP clients and jobs.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();

        services
            .AddAmbientContext()
            .AddSecurityPrimitives(configuration)
            .AddDistributedCache(configuration)
            .AddResilience(configuration)
            .AddWhatsAppClient(configuration)
            .AddBackgroundJobs();

        return services;
    }

    private static IServiceCollection AddAmbientContext(this IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Scoped: these read per-request state, and a singleton would capture the first request's
        // HttpContext for the lifetime of the process.
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IRequestContext, HttpRequestContext>();

        return services;
    }

    private static IServiceCollection AddSecurityPrimitives(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            // ValidateOnStart turns a missing signing key into a startup failure rather than a
            // 500 on the first sign-in attempt in production.
            .ValidateOnStart();

        services.AddOptions<PasswordHashingOptions>()
            .Bind(configuration.GetSection(PasswordHashingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        return services;
    }

    private static IServiceCollection AddDistributedCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<RedisConnection>();

        // Constructed by hand rather than by convention, because the multiplexer is legitimately
        // nullable and the container has no way to express "inject null if the dependency could
        // not be established".
        services.AddSingleton<ICacheService>(provider => new RedisCacheService(
            provider.GetRequiredService<RedisConnection>().Multiplexer,
            provider.GetRequiredService<IOptions<RedisOptions>>(),
            provider.GetRequiredService<IDateTimeProvider>(),
            provider.GetRequiredService<ILogger<RedisCacheService>>()));

        return services;
    }

    private static IServiceCollection AddResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ResilienceOptions>()
            .Bind(configuration.GetSection(ResilienceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddResiliencePipeline(ResiliencePipelineNames.GeneralOutbound, (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<ResilienceOptions>>().Value;
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(ResiliencePipelineNames.GeneralOutbound);

            ResiliencePipelineFactory.Configure(builder, options, logger);
        });

        return services;
    }

    private static IServiceCollection AddWhatsAppClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WhatsAppOptions>()
            .Bind(configuration.GetSection(WhatsAppOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddTransient<GraphApiErrorHandler>();

        var whatsAppOptions = configuration
            .GetSection(WhatsAppOptions.SectionName)
            .Get<WhatsAppOptions>() ?? new WhatsAppOptions();

        services
            .AddRefitClient<IWhatsAppCloudApi>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri($"{whatsAppOptions.BaseUrl.TrimEnd('/')}/{whatsAppOptions.ApiVersion}/");
                client.Timeout = TimeSpan.FromSeconds(whatsAppOptions.RequestTimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .AddHttpMessageHandler<GraphApiErrorHandler>()
            // Retry, circuit breaker, per-attempt and total timeouts. Configured centrally so no
            // call site can opt out, and so a Meta incident degrades this platform's throughput
            // rather than exhausting its request threads.
            .AddStandardResilienceHandler(resilience =>
            {
                var options = whatsAppOptions;

                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
                resilience.TotalRequestTimeout.Timeout =
                    TimeSpan.FromSeconds(options.RequestTimeoutSeconds * 3);
                resilience.CircuitBreaker.SamplingDuration =
                    TimeSpan.FromSeconds(options.RequestTimeoutSeconds * 4);
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));

        return services;
    }
}
