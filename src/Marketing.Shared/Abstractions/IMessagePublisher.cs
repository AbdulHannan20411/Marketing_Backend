namespace Marketing.Shared.Abstractions;

/// <summary>
/// Publishes an event to whoever is interested in it.
/// <para>
/// Deliberately says nothing about exchanges, routing keys or brokers. The layer above states what
/// happened; the transport decides where that goes. This replaced an earlier interface that took an
/// exchange and a routing key, which put RabbitMQ's topology into the signature and meant callers in
/// the Application layer had to know how the broker was laid out.
/// </para>
/// <para>
/// Publish is fire-and-forget by design: it tells subscribers something has happened and does not
/// wait for them. Work that must happen exactly once, in step with a database transaction, belongs
/// in the import outbox rather than here — see <c>IImportJobDispatcher</c>.
/// </para>
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes a message to every subscriber of its type.
    /// </summary>
    /// <remarks>
    /// The message type is the address. Routing is derived from <typeparamref name="TMessage"/>, so
    /// adding a subscriber never requires the publisher to change.
    /// </remarks>
    /// <typeparam name="TMessage">Message contract. Must be a reference type.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class;
}
