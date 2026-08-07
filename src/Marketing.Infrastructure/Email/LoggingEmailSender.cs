using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.Email;

/// <summary>
/// Records a message instead of delivering it.
/// <para>
/// The honest stand-in until a provider is configured. It logs the recipient, the subject and the
/// full link at warning level, so an invitation can be completed end to end in development by
/// copying the link out of the log - and so nobody in production can mistake the platform for
/// having actually sent mail.
/// </para>
/// </summary>
public sealed partial class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="logger">Logger.</param>
    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string TransportName => "log-only";

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Warning, not information: an email that was never sent is a degraded state, and it
        // should be visible as one rather than buried in routine output.
        LogMessageNotDelivered(message.ToAddress, message.Subject, message.TextBody);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Warning,
        Message = "EMAIL NOT SENT (no provider configured). To: {ToAddress}. Subject: {Subject}\n{Body}")]
    private partial void LogMessageNotDelivered(string toAddress, string subject, string body);
}
