namespace Marketing.Shared.Abstractions;

/// <summary>
/// Delivers transactional email.
/// <para>
/// A seam, like the payment gateway. No provider is configured, so the current implementation
/// records the message rather than delivering it - which lets the whole invitation and reset flow
/// be built, tested and exercised today. Introducing SMTP or a provider replaces one class.
/// </para>
/// </summary>
public interface IEmailSender
{
    /// <summary>Name of the transport, recorded in logs so nobody mistakes a stub for delivery.</summary>
    public string TransportName { get; }

    /// <summary>
    /// Sends a message.
    /// <para>
    /// Callers must treat failure as non-fatal for anything already committed. An invitation whose
    /// email bounced is still a created account, and rolling the account back would be worse.
    /// </para>
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>A transactional email.</summary>
/// <param name="ToAddress">Recipient address.</param>
/// <param name="ToName">Recipient display name.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="HtmlBody">HTML body.</param>
/// <param name="TextBody">Plain-text alternative, for clients that will not render HTML.</param>
public sealed record EmailMessage(
    string ToAddress,
    string ToName,
    string Subject,
    string HtmlBody,
    string TextBody);
