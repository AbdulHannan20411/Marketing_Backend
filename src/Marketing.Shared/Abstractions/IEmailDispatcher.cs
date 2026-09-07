namespace Marketing.Shared.Abstractions;

/// <summary>
/// Actually delivers a message to a relay.
/// </summary>
/// <remarks>
/// Split from <see cref="IEmailSender"/> so the two responsibilities cannot be confused. Everything
/// in the application queues through <c>IEmailSender</c> and returns immediately; only the outbox
/// poller delivers, and it is the only thing that should ever wait on a network round trip to a
/// mail server.
/// <para>
/// The split is what stops the old behaviour reappearing: a service that injects this interface by
/// mistake is doing something visibly different from every other caller, rather than silently
/// putting an SMTP conversation back inside a request.
/// </para>
/// </remarks>
public interface IEmailDispatcher
{
    /// <summary>Name of the transport, recorded so nobody mistakes a stub for delivery.</summary>
    public string TransportName { get; }

    /// <summary>
    /// Delivers a message, throwing when the relay refuses it.
    /// </summary>
    /// <remarks>
    /// Unlike the queueing seam, failure here is meaningful: the caller is the poller, and it
    /// decides whether to retry or give up.
    /// </remarks>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
