using System.Collections.Concurrent;
using AwesomeAssertions;
using MassTransit;
using Marketing.Common.Exceptions;
using Marketing.Infrastructure.Messaging;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marketing.IntegrationTests.Messaging;

/// <summary>
/// Exercises the bus against a real RabbitMQ container, through the production registration.
/// </summary>
/// <remarks>
/// A real broker rather than MassTransit's in-memory harness, because what is worth proving here is
/// the part the harness replaces: that the topology is declared, that a published message is routed
/// to the right queue, and that the retry policy attached to the receive endpoint behaves as
/// configured.
/// </remarks>
[Collection(nameof(MessagingFixtureGroup))]
public sealed class MessagingTests
{
    /// <summary>How long to wait for delivery before calling it a failure.</summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    private readonly MessagingHostFixture _fixture;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="fixture">Shared containers.</param>
    public MessagingTests(MessagingHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>A published message reaches its consumer.</summary>
    [Fact]
    public async Task PublishAsync_DeliversTheMessageToItsConsumer()
    {
        var correlation = MessageProbe.NewCorrelationId();

        using var host = await _fixture.StartHostAsync(bus => bus.AddConsumer<ProbeConsumer>());

        await PublishAsync(host, new ProbeMessage(correlation, ShouldThrow: false));

        var delivered = await MessageProbe.WaitForAttemptsAsync(correlation, 1, DeliveryTimeout);

        delivered.Should().Be(1, "a published message should reach its consumer exactly once");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A transient failure is retried, and the retries are bounded.</summary>
    /// <remarks>
    /// Both halves matter. Retrying proves the policy is attached to the endpoint; stopping proves
    /// it is bounded, which is what keeps one poisonous message from occupying a consumer for ever.
    /// </remarks>
    [Fact]
    public async Task Consume_RetriesATransientFailure_AndStopsAfterThree()
    {
        var correlation = MessageProbe.NewCorrelationId();

        using var host = await _fixture.StartHostAsync(bus => bus.AddConsumer<ProbeConsumer>());

        await PublishAsync(host, new ProbeMessage(correlation, ShouldThrow: true));

        // The configured policy is three retries after the first attempt, five seconds apart.
        var attempts = await MessageProbe.WaitForAttemptsAsync(correlation, 4, TimeSpan.FromSeconds(90));

        attempts.Should().Be(4, "one delivery plus the three configured retries");

        // Longer than one retry interval, to show the count has settled rather than still climbing.
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        MessageProbe.Attempts(correlation).Should().Be(4, "retries are bounded");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A permanent business failure is not retried at all.</summary>
    [Fact]
    public async Task Consume_DoesNotRetryAPermanentFailure()
    {
        var correlation = MessageProbe.NewCorrelationId();

        using var host = await _fixture.StartHostAsync(bus => bus.AddConsumer<PermanentFailureConsumer>());

        await PublishAsync(host, new PermanentFailureMessage(correlation));

        await MessageProbe.WaitForAttemptsAsync(correlation, 1, DeliveryTimeout);

        // Twice the retry interval. A retried message would show a second attempt by now.
        await Task.Delay(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);

        MessageProbe.Attempts(correlation).Should()
            .Be(1, "a broken business rule fails identically on every attempt, so it is not retried");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The receive endpoint is named after the consumer, kebab-cased.</summary>
    /// <remarks>
    /// Pinned deliberately. A queue name is the contract between a deployment and its broker, and a
    /// silent rename strands whatever is already sitting in the old queue.
    /// </remarks>
    [Fact]
    public async Task Topology_NamesTheQueueAfterTheConsumer()
    {
        using var host = await _fixture.StartHostAsync(bus => bus.AddConsumer<ProbeConsumer>());

        var formatter = host.Services.GetRequiredService<IEndpointNameFormatter>();

        formatter.Consumer<ProbeConsumer>().Should().Be("probe");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>With messaging switched off, no bus is built and publishing is a no-op.</summary>
    [Fact]
    public async Task Disabled_BuildsNoBus_AndDropsMessages()
    {
        using var host = await _fixture.StartHostAsync(
            overrides: new Dictionary<string, string?> { ["RabbitMQ:Enabled"] = "false" },
            waitForBus: false);

        host.Services.GetService<IBusControl>().Should().BeNull("no bus is registered when disabled");

        await using var scope = host.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();

        publisher.Should().BeOfType<NullMessagePublisher>();

        // A no-op rather than a throw, so a caller added later is not broken by the absence of a
        // broker.
        await publisher.Invoking(candidate =>
                candidate.PublishAsync(new ProbeMessage("disabled", false), TestContext.Current.CancellationToken))
            .Should().NotThrowAsync();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An unreachable broker does not stop the host starting.</summary>
    /// <remarks>
    /// The behaviour most easily lost when a messaging library is introduced. MassTransit is
    /// perfectly capable of blocking startup until the bus connects, and a service that will not
    /// boot without RabbitMQ is a materially different operational proposition from one that
    /// degrades.
    /// </remarks>
    [Fact]
    public async Task UnreachableBroker_DoesNotBlockStartup()
    {
        using var host = await _fixture.StartHostAsync(
            overrides: new Dictionary<string, string?>
            {
                // A port nothing is listening on.
                ["RabbitMQ:ConnectionString"] = "amqp://guest:guest@127.0.0.1:5673/",
            },
            waitForBus: false);

        // Reaching this line is the assertion: StartHostAsync awaited StartAsync and returned.
        await using var scope = host.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IMessagePublisher>().Should()
            .BeOfType<MassTransitMessagePublisher>("the bus is configured, merely unreachable");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task PublishAsync<TMessage>(IHost host, TMessage message)
        where TMessage : class
    {
        await using var scope = host.Services.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();

        await publisher.PublishAsync(message, TestContext.Current.CancellationToken);
    }
}

/// <summary>Message used to observe delivery and retry behaviour.</summary>
/// <param name="CorrelationId">Ties an observation back to the test that published it.</param>
/// <param name="ShouldThrow">Whether the consumer should fail transiently.</param>
public sealed record ProbeMessage(string CorrelationId, bool ShouldThrow);

/// <summary>Message whose consumer always fails for a reason no retry can fix.</summary>
/// <param name="CorrelationId">Ties an observation back to the test that published it.</param>
public sealed record PermanentFailureMessage(string CorrelationId);

/// <summary>Records each delivery, and fails transiently on demand.</summary>
public sealed class ProbeConsumer : IConsumer<ProbeMessage>
{
    /// <inheritdoc />
    public Task Consume(ConsumeContext<ProbeMessage> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MessageProbe.Record(context.Message.CorrelationId);

        return context.Message.ShouldThrow
            ? throw new InvalidOperationException("Transient failure, raised by the retry test.")
            : Task.CompletedTask;
    }
}

/// <summary>Always fails with an exception the retry policy is told to ignore.</summary>
public sealed class PermanentFailureConsumer : IConsumer<PermanentFailureMessage>
{
    /// <inheritdoc />
    public Task Consume(ConsumeContext<PermanentFailureMessage> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MessageProbe.Record(context.Message.CorrelationId);

        throw new BusinessRuleException("probe_permanent", "Permanent failure, raised by the retry test.");
    }
}

/// <summary>Counts deliveries per correlation id, across consumer instances.</summary>
/// <remarks>
/// Static because MassTransit constructs a consumer per message and the assertions are about how
/// many times that happened. Keyed by a fresh correlation id per test, so tests sharing the
/// containers cannot read one another's counts.
/// </remarks>
public static class MessageProbe
{
    private static readonly ConcurrentDictionary<string, int> Deliveries = new(StringComparer.Ordinal);

    /// <summary>A correlation id no other test will use.</summary>
    public static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary>Records one delivery.</summary>
    /// <param name="correlationId">Correlation id from the message.</param>
    public static void Record(string correlationId) =>
        Deliveries.AddOrUpdate(correlationId, 1, (_, count) => count + 1);

    /// <summary>Deliveries recorded so far.</summary>
    /// <param name="correlationId">Correlation id from the message.</param>
    public static int Attempts(string correlationId) => Deliveries.GetValueOrDefault(correlationId);

    /// <summary>Waits until a correlation id has been delivered a given number of times.</summary>
    /// <param name="correlationId">Correlation id from the message.</param>
    /// <param name="expected">Deliveries to wait for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The count reached, which falls short of <paramref name="expected"/> on timeout.</returns>
    public static async Task<int> WaitForAttemptsAsync(string correlationId, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && Attempts(correlationId) < expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return Attempts(correlationId);
    }
}
