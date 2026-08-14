using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.Email;

/// <summary>
/// An in-process mailbox holding the last few messages the platform tried to send.
/// <para>
/// Exists because invitation and reset tokens are stored <em>hashed</em> — the raw token is
/// generated once, put in a link, and never persisted. There is therefore no way to look one up
/// afterwards, and no endpoint could reveal one. Capturing the outgoing message is the only way to
/// complete those flows without a mail provider.
/// </para>
/// <para>
/// <b>Development only.</b> It holds live credentials in memory in plain text; it is registered
/// nowhere else, and the endpoint that reads it refuses to exist outside development.
/// </para>
/// </summary>
public sealed partial class DevMailbox
{
    /// <summary>
    /// Messages retained. Small on purpose: this is a debugging aid, not a mail store, and an
    /// unbounded list of messages containing live tokens is a liability even on a laptop.
    /// </summary>
    private const int Capacity = 50;

    private readonly ConcurrentQueue<CapturedEmail> _messages = new();

    /// <summary>Records a message, discarding the oldest once the mailbox is full.</summary>
    /// <param name="message">The message that would have been sent.</param>
    /// <param name="sentAt">When it was captured.</param>
    public void Add(EmailMessage message, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        _messages.Enqueue(new CapturedEmail(
            message.ToAddress,
            message.ToName,
            message.Subject,
            message.TextBody,
            message.HtmlBody,
            ExtractLink(message.TextBody) ?? ExtractLink(message.HtmlBody),
            message.ReplyToAddress,
            message.ReplyToName,
            sentAt));

        while (_messages.Count > Capacity && _messages.TryDequeue(out _))
        {
            // Intentionally empty: dequeuing is the whole operation.
        }
    }

    /// <summary>Returns every captured message, newest first.</summary>
    public IReadOnlyList<CapturedEmail> All() =>
        [.. _messages.Reverse()];

    /// <summary>Discards everything captured so far.</summary>
    public void Clear()
    {
        while (_messages.TryDequeue(out _))
        {
            // Intentionally empty.
        }
    }

    /// <summary>
    /// Pulls the first URL out of a body, so the caller does not have to parse the message.
    /// </summary>
    /// <param name="body">Message body.</param>
    private static string? ExtractLink(string? body) =>
        body is null ? null : LinkPattern().Match(body) is { Success: true } match ? match.Value : null;

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex LinkPattern();
}

/// <summary>One message the platform tried to send.</summary>
/// <param name="ToAddress">Recipient address.</param>
/// <param name="ToName">Recipient display name.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="TextBody">Plain-text body.</param>
/// <param name="HtmlBody">HTML body.</param>
/// <param name="Link">
/// The first link found in the body — the activation or reset URL, which carries the single-use
/// token. This is the value you need to finish the flow.
/// </param>
/// <param name="ReplyToAddress">
/// Address replies go to, when the message was caused by somebody in a workspace. Captured because
/// attribution is a deliverability feature — a message that reaches the inbox but replies to a
/// no-reply mailbox has only half worked, and that half is invisible without this.
/// </param>
/// <param name="ReplyToName">Display name for the reply address.</param>
/// <param name="SentAt">When it was captured.</param>
public sealed record CapturedEmail(
    string ToAddress,
    string ToName,
    string Subject,
    string TextBody,
    string HtmlBody,
    string? Link,
    string? ReplyToAddress,
    string? ReplyToName,
    DateTimeOffset SentAt);

/// <summary>
/// Logs a message and keeps a copy in <see cref="DevMailbox"/>.
/// <para>
/// Replaces <see cref="LoggingEmailSender"/> in development. It still logs, so the existing
/// behaviour is unchanged; the copy simply makes the link readable over HTTP instead of only by
/// scrolling the console.
/// </para>
/// </summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly DevMailbox _mailbox;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<CapturingEmailSender> _logger;

    /// <summary>Initialises a new instance.</summary>
    public CapturingEmailSender(
        DevMailbox mailbox,
        IDateTimeProvider clock,
        ILogger<CapturingEmailSender> logger)
    {
        _mailbox = mailbox;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TransportName => "dev-mailbox";

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _mailbox.Add(message, _clock.UtcNow);

        LogMessageCaptured(message.ToAddress, message.Subject);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Warning,
        Message = "EMAIL NOT SENT (development mailbox). To: {ToAddress}. Subject: {Subject}. "
                  + "Read it at GET /api/v1/dev/emails.")]
    private partial void LogMessageCaptured(string toAddress, string subject);
}
