using Marketing.Infrastructure.Authentication;
using Marketing.Infrastructure.Logging;
using Marketing.Infrastructure.Payments;
using Marketing.Infrastructure.Redis;
using Marketing.Infrastructure.Security;
using Marketing.Infrastructure.Resilience;
using Marketing.Infrastructure.Time;
using Marketing.Infrastructure.WhatsApp;
using Marketing.Infrastructure.WhatsApp.Clients;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
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
            .AddWhatsAppClient(configuration);

        // No payment provider is configured. The manual gateway records offline settlements so the
        // billing flow works end to end; swapping in a provider replaces this one registration.
        services.AddScoped<Application.Interfaces.IPaymentGateway, ManualPaymentGateway>();

        services.AddOptions<Application.Configurations.SmtpOptions>()
            .Bind(configuration.GetSection(Application.Configurations.SmtpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var smtp = configuration
            .GetSection(Application.Configurations.SmtpOptions.SectionName)
            .Get<Application.Configurations.SmtpOptions>();

        if (smtp?.IsConfigured == true)
        {
            services.AddSingleton<IEmailSender, Email.SmtpEmailSender>();
        }
        else
        {
            // No relay host configured. The logging sender records the message and its link so
            // invitations and resets can be completed end to end; it is deliberately the fallback
            // rather than something anyone has to switch off.
            services.AddSingleton<IEmailSender, Email.LoggingEmailSender>();
        }

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

        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .ValidateDataAnnotations()
            // Without a key, every stored WhatsApp token becomes unreadable. Better to refuse to
            // start than to discover that on the first campaign send.
            .ValidateOnStart();

        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

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
        services.AddTransient<TenantAccessTokenHandler>();

        // The seam the Application layer depends on. Everything above this line is Refit, Polly and
        // Graph payload shapes; nothing above it is visible to a service.
        services.AddScoped<Application.Interfaces.IWhatsAppGateway, MetaWhatsAppGateway>();

        // Singleton: it holds only the app secret and does no per-request work.
        services.AddSingleton<Application.Interfaces.IWhatsAppWebhookVerifier, MetaWebhookVerifier>();

        var whatsAppOptions = configuration
            .GetSection(WhatsAppOptions.SectionName)
            .Get<WhatsAppOptions>() ?? new WhatsAppOptions();

        var clientBuilder = services
            .AddRefitClient<IWhatsAppCloudApi>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri($"{whatsAppOptions.BaseUrl.TrimEnd('/')}/{whatsAppOptions.ApiVersion}/");
                client.Timeout = TimeSpan.FromSeconds(whatsAppOptions.RequestTimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            // Order matters. The token handler runs first so the credential is attached before the
            // error handler sees the response, and the error handler is therefore the last thing
            // between Meta and the caller.
            .AddHttpMessageHandler<TenantAccessTokenHandler>()
            .AddHttpMessageHandler<GraphApiErrorHandler>();

        // Recycles pooled connection handlers so DNS changes are picked up; a handler pinned for
        // the process lifetime keeps talking to an IP address Meta has since moved away from.
        clientBuilder.SetHandlerLifetime(TimeSpan.FromMinutes(5));

        // Retry, circuit breaker, per-attempt and total timeouts. Configured centrally so no call
        // site can opt out, and so a Meta incident degrades this platform's throughput rather than
        // exhausting its request threads. Applied last so it wraps the error handler.
        clientBuilder.AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(whatsAppOptions.RequestTimeoutSeconds);
            resilience.TotalRequestTimeout.Timeout =
                TimeSpan.FromSeconds(whatsAppOptions.RequestTimeoutSeconds * 3);
            resilience.CircuitBreaker.SamplingDuration =
                TimeSpan.FromSeconds(whatsAppOptions.RequestTimeoutSeconds * 4);
        });

        return services;
    }
}
