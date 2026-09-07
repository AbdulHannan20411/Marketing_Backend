using MassTransit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    /// <param name="configureBus">
    /// Adds consumers and other registrations to the message bus.
    /// <para>
    /// A seam rather than a convenience: MassTransit permits exactly one <c>AddMassTransit</c> call
    /// per container, so anything that needs to contribute a consumer — another module, or a test —
    /// has to do it inside the single registration this method owns.
    /// </para>
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureBus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();

        services
            .AddAmbientContext()
            .AddSecurityPrimitives(configuration)
            .AddDistributedCache(configuration)
            .AddMessaging(configuration, configureBus)
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
            services.AddSingleton<IEmailDispatcher, Email.SmtpEmailSender>();
        }
        else
        {
            // No relay host configured. The logging sender records the message and its link so
            // invitations and resets can be completed end to end; it is deliberately the fallback
            // rather than something anyone has to switch off.
            services.AddSingleton<IEmailDispatcher, Email.LoggingEmailSender>();
        }

        // Everything in the application queues; only the outbox poller resolves the dispatcher
        // above and waits on a relay. Registered scoped because it writes through the caller's own
        // unit of work, so the queued message commits with whatever caused it.
        services.AddScoped<IEmailSender, Application.Services.Email.OutboxEmailSender>();
        services.AddScoped<Application.Services.Email.IEmailOutboxProcessor,
            Application.Services.Email.EmailOutboxProcessor>();

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

            // ConnectionStrings:Redis wins when it is set. It is where operators and every hosting
            // platform expect a connection string to live, and binding it here means the deployed
            // value does not have to be duplicated into the Redis section to take effect.
            .PostConfigure<IConfiguration>((options, config) =>
            {
                var shared = config.GetConnectionString("Redis");

                if (!string.IsNullOrWhiteSpace(shared))
                {
                    options.ConnectionString = shared;
                }
            })
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

    /// <summary>Registers the broker connection and publisher.</summary>
    private static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureBus)
    {
        services.AddOptions<Messaging.RabbitMqOptions>()
            .Bind(configuration.GetSection(Messaging.RabbitMqOptions.SectionName))

            // ConnectionStrings:RabbitMq wins when it is set. It is where operators and every
            // hosting platform expect a connection string to live, so the deployed value does not
            // have to be duplicated into the RabbitMQ section to take effect.
            .PostConfigure<IConfiguration>((options, config) =>
            {
                var shared = config.GetConnectionString("RabbitMq");

                if (!string.IsNullOrWhiteSpace(shared))
                {
                    options.ConnectionString = shared;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = configuration
            .GetSection(Messaging.RabbitMqOptions.SectionName)
            .Get<Messaging.RabbitMqOptions>() ?? new Messaging.RabbitMqOptions();

        if (!options.Enabled)
        {
            // No bus at all. Registering one against an address nobody runs would make every
            // deployment without a broker log connection failures for ever, and MassTransit's
            // health check would sit permanently degraded for a feature that is switched off.
            // Scoped, matching the MassTransit-backed publisher, so a consumer of this interface
            // sees one lifetime whichever implementation is registered.
            services.AddScoped<IMessagePublisher, Messaging.NullMessagePublisher>();

            return services;
        }

        services.AddMassTransit(bus =>
        {
            // Queue names come from consumer names, kebab-cased. Set explicitly rather than left to
            // the default so the topology is a property of this configuration and does not shift if
            // MassTransit changes its default formatter.
            bus.SetKebabCaseEndpointNameFormatter();

            // MassTransit's own bus health check, retagged. By default it reports Unhealthy and
            // carries the "ready" tag, which would pull instances out of rotation whenever the
            // broker blipped — the opposite of the degraded-start behaviour this API is built for.
            // Degraded and tagged "messaging" keeps it visible without making it fatal, matching
            // how the cache is treated.
            bus.ConfigureHealthCheckOptions(health =>
            {
                health.Name = "rabbitmq";
                health.MinimalFailureStatus = HealthStatus.Degraded;
                health.Tags.Clear();
                health.Tags.Add("messaging");
            });

            // Consumers are contributed here. The platform ships none yet — its asynchronous work
            // runs on the import outbox, which is deliberately untouched — so this is the seam that
            // will carry them, and what the messaging tests use to attach one.
            configureBus?.Invoke(bus);

            bus.UsingRabbitMq((context, configurator) =>
            {
                ConfigureHost(configurator, options);

                // Retries live here rather than in a loop around a publish. Applied to the receive
                // endpoint, so a consumer that throws is retried by the transport with the message
                // still on the queue, and lands in <queue>_error once the attempts are spent.
                configurator.UseMessageRetry(retry =>
                {
                    retry.Interval(3, TimeSpan.FromSeconds(5));

                    // Permanent failures are not retried. A malformed message and a broken business
                    // rule will fail identically on the third attempt as on the first; retrying
                    // only delays the dead letter and multiplies the side effects of getting there.
                    retry.Ignore<Common.Exceptions.ValidationException>();
                    retry.Ignore<Common.Exceptions.BusinessRuleException>();
                    retry.Ignore<Common.Exceptions.NotFoundException>();
                    retry.Ignore<ArgumentException>();
                });

                // Safe here, and checked before use: the platform declares no queues, exchanges or
                // routing keys of its own today, so there is no existing topology for this to
                // rename. Once a consumer ships against a deployed queue, pin it with a
                // ReceiveEndpoint instead of letting the formatter decide.
                configurator.ConfigureEndpoints(context);
            });
        });

        // Startup must not block on the broker. The API's existing behaviour is to start and serve
        // traffic whether or not its messaging dependency is reachable, and MassTransit connects in
        // the background and keeps retrying. Set explicitly rather than relied on as a default.
        services.Configure<MassTransitHostOptions>(host => host.WaitUntilStarted = false);

        // Scoped, because IPublishEndpoint is. Inside a consumer the scoped endpoint carries the
        // incoming message's context, which is what lets MassTransit correlate a published message
        // to the one that caused it. A singleton here would be a captive dependency and would lose
        // that correlation.
        services.AddScoped<IMessagePublisher, Messaging.MassTransitMessagePublisher>();

        return services;
    }

    /// <summary>Points the bus at the configured broker.</summary>
    private static void ConfigureHost(
        IRabbitMqBusFactoryConfigurator configurator,
        Messaging.RabbitMqOptions options)
    {
        void Credentials(IRabbitMqHostConfigurator host)
        {
            host.Username(options.Username);
            host.Password(options.Password);
            host.Heartbeat(TimeSpan.FromSeconds(options.RequestedHeartbeatSeconds));
            host.RequestedConnectionTimeout(TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds));
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            // A whole connection string replaces the discrete settings, matching what the option
            // documents. Credentials inside the URI are honoured by the transport and are never
            // logged.
            configurator.Host(new Uri(options.ConnectionString), Credentials);

            return;
        }

        configurator.Host(options.Host, (ushort)options.Port, options.VirtualHost, Credentials);
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

        services.AddOptions<Storage.StorageOptions>()
            .Bind(configuration.GetSection(Storage.StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Local disk is correct for one instance. A multi-instance deployment needs the same
        // interface over shared storage, because the worker that reads an upload will not be on the
        // node that received it.
        services.AddSingleton<Shared.Abstractions.IFileStorage, Storage.LocalFileStorage>();

        // Stateless and thread-safe, so one instance of each serves every import.
        services.AddSingleton<Application.Services.Imports.IImportFileReader, Imports.CsvImportFileReader>();
        services.AddSingleton<Application.Services.Imports.IImportFileReader, Imports.ExcelImportFileReader>();
        services.AddSingleton<
            Application.Services.Imports.IImportFileReaderFactory, Imports.ImportFileReaderFactory>();
        services.AddSingleton<
            Application.Services.Imports.IImportErrorReportWriter, Imports.ExcelImportErrorReportWriter>();

        // Stateless, and the font resolver behind it caches on first use, so one instance serves
        // every invoice download.
        services.AddSingleton<
            Application.Services.Billing.IInvoiceRenderer, Billing.MigraDocInvoiceRenderer>();

        // Places provider. Bound even when no key is present: an unset key is a supported state
        // meaning "this deployment does not offer business discovery", which the endpoints report
        // cleanly rather than failing at startup over a feature most deployments will not use.
        services.Configure<Places.PlacesOptions>(configuration.GetSection(Places.PlacesOptions.SectionName));

        services.AddHttpClient<Application.Interfaces.IPlaceProvider, Places.GooglePlacesProvider>(
            client => client.Timeout = TimeSpan.FromSeconds(15));

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
                // No trailing slash. Refit demands a leading slash on every route and joins the
                // two by concatenation, so one here would produce https://host/v21.0//{id}. Meta
                // tolerates the doubled separator, but it reads as a defect in every logged URL.
                client.BaseAddress = new Uri(
                    $"{whatsAppOptions.BaseUrl.TrimEnd('/')}/{whatsAppOptions.ApiVersion.Trim('/')}");
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
