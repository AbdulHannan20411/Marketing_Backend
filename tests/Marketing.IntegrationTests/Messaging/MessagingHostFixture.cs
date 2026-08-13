using MassTransit;
using Marketing.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Marketing.IntegrationTests.Messaging;

/// <summary>
/// Builds hosts that run the real infrastructure registration against throwaway containers.
/// </summary>
/// <remarks>
/// A plain generic host rather than the API factory, because these tests are about the bus rather
/// than about HTTP, and because consumers have to be contributed through
/// <c>AddInfrastructure</c>'s bus callback — MassTransit permits one <c>AddMassTransit</c> per
/// container, so a test cannot bolt a second registration onto an already-built API host.
/// <para>Requires a working Docker daemon.</para>
/// </remarks>
public sealed class MessagingHostFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine")
        .WithCleanUp(true)
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine")
        .WithCleanUp(true)
        .Build();

    /// <summary>AMQP address of the throwaway broker, credentials included.</summary>
    public string BrokerConnectionString => _rabbitMq.GetConnectionString();

    /// <inheritdoc />
    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(_rabbitMq.StartAsync(), _redis.StartAsync());

    /// <inheritdoc />
    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(_rabbitMq.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());

    /// <summary>
    /// Builds and starts a host wired exactly as production wires itself.
    /// </summary>
    /// <param name="configureBus">Consumers to attach.</param>
    /// <param name="overrides">Configuration values applied over the defaults.</param>
    /// <param name="waitForBus">
    /// Whether to wait for the bus to report healthy before returning. Startup does not block on the
    /// broker, so a publish issued the instant a host starts can be dropped before the topology
    /// exists — tests that publish must wait, and one test deliberately does not.
    /// </param>
    public async Task<IHost> StartHostAsync(
        Action<IBusRegistrationConfigurator>? configureBus = null,
        IDictionary<string, string?>? overrides = null,
        bool waitForBus = true)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RabbitMQ:ConnectionString"] = BrokerConnectionString,
            ["RabbitMQ:Enabled"] = "true",

            ["Redis:ConnectionString"] = _redis.GetConnectionString(),
            ["Redis:Enabled"] = "true",

            // Everything below is required by options validation in the infrastructure
            // registration. None of it is exercised by these tests; it is here so the host starts.
            ["Authentication:Jwt:Issuer"] = "https://api.integration.test",
            ["Authentication:Jwt:Audience"] = "integration-tests",
            ["Authentication:Jwt:SigningKey"] = "integration-test-signing-key-long-enough-for-hmac-sha256",
            ["Security:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["WhatsApp:AppId"] = "000000000000000",
            ["WhatsApp:AppSecret"] = "integration-test-secret",
            ["WhatsApp:WebhookVerifyToken"] = "integration-verify-token",
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddInfrastructure(builder.Configuration, configureBus);

        var host = builder.Build();

        await host.StartAsync();

        if (waitForBus && host.Services.GetService<IBusControl>() is { } bus)
        {
            await bus.WaitForHealthStatus(BusHealthStatus.Healthy, TimeSpan.FromSeconds(30));
        }

        return host;
    }
}

/// <summary>Shares one pair of containers across the messaging tests.</summary>
[CollectionDefinition(nameof(MessagingFixtureGroup), DisableParallelization = true)]
public sealed class MessagingFixtureGroup : ICollectionFixture<MessagingHostFixture>;
