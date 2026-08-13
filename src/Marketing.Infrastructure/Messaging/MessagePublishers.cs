using MassTransit;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.Messaging;

/// <summary>
/// Publishes through MassTransit.
/// </summary>
/// <remarks>
/// A thin adapter, and intentionally so. It exists to keep <see cref="IMessagePublisher"/> the only
/// messaging type the layers above see; everything about how a message reaches the broker —
/// serialisation, topology, connection lifetime, reconnection — is MassTransit's.
/// </remarks>
public sealed partial class MassTransitMessagePublisher : IMessagePublisher
{
    private readonly IPublishEndpoint _endpoint;
    private readonly ILogger<MassTransitMessagePublisher> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="endpoint">MassTransit publish endpoint.</param>
    /// <param name="logger">Logger.</param>
    public MassTransitMessagePublisher(
        IPublishEndpoint endpoint,
        ILogger<MassTransitMessagePublisher> logger)
    {
        _endpoint = endpoint;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        await _endpoint.Publish(message, cancellationToken);

        // The type only. A message body can carry a contact's number, an email address or a token,
        // and a debug log is the easiest place in a system to leak one.
        LogPublished(typeof(TMessage).Name);
    }

    [LoggerMessage(
        EventId = 2950,
        Level = LogLevel.Debug,
        Message = "Published a {MessageType} message.")]
    private partial void LogPublished(string messageType);
}

/// <summary>
/// Accepts messages and drops them, for when messaging is switched off.
/// </summary>
/// <remarks>
/// The same shape as the fallback email sender: the feature is absent rather than broken, so the
/// rest of the application runs unchanged and a deployment with no broker is a supported
/// configuration rather than a crash. Logged at warning, because silently discarding a message is
/// not something anyone should discover from behaviour alone.
/// </remarks>
public sealed partial class NullMessagePublisher : IMessagePublisher
{
    private readonly ILogger<NullMessagePublisher> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="logger">Logger.</param>
    public NullMessagePublisher(ILogger<NullMessagePublisher> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        LogDropped(typeof(TMessage).Name);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2951,
        Level = LogLevel.Warning,
        Message = "Messaging is disabled; a {MessageType} message was dropped.")]
    private partial void LogDropped(string messageType);
}
