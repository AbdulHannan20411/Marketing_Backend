using MailKit.Net.Smtp;
using MailKit.Security;
using Marketing.Application.Configurations;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Marketing.Infrastructure.Email;

/// <summary>
/// Delivers mail over SMTP.
/// <para>
/// MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which Microsoft marks obsolete for new
/// work and which does not negotiate STARTTLS correctly against modern relays.
/// </para>
/// <para>
/// A new connection per message. Pooling would be faster, but this platform sends a handful of
/// transactional messages a day, and a pooled connection that a relay silently drops fails the next
/// send for reasons nobody can reproduce.
/// </para>
/// </summary>
public sealed partial class SmtpEmailSender : IEmailDispatcher
{
    private readonly SmtpOptions _smtp;
    private readonly EmailOptions _email;
    private readonly ILogger<SmtpEmailSender> _logger;

    /// <summary>Initialises a new instance.</summary>
    public SmtpEmailSender(
        IOptions<SmtpOptions> smtp,
        IOptions<EmailOptions> email,
        ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(email);

        _smtp = smtp.Value;
        _email = email.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TransportName => $"smtp:{_smtp.Host}";

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = new MimeMessage();

        // Most relays refuse to send as an address the authenticated account does not own, and
        // Gmail silently rewrites it. The configured from-address must therefore be the account.
        mime.From.Add(new MailboxAddress(_email.FromName, _email.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));

        // Attribution, when the message was caused by somebody in a workspace. A reply then reaches
        // the colleague who sent the invitation rather than a no-reply mailbox nobody reads.
        if (!string.IsNullOrWhiteSpace(message.ReplyToAddress))
        {
            mime.ReplyTo.Add(new MailboxAddress(message.ReplyToName ?? string.Empty, message.ReplyToAddress));
        }
        mime.Subject = message.Subject;

        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        }.ToMessageBody();

        using var client = new SmtpClient
        {
            Timeout = (int)TimeSpan.FromSeconds(_smtp.TimeoutSeconds).TotalMilliseconds,
        };

        try
        {
            await client.ConnectAsync(
                _smtp.Host,
                _smtp.Port,
                // StartTlsWhenAvailable would silently accept a plaintext session against a relay
                // that stopped advertising STARTTLS. Requiring it means a downgrade fails loudly.
                _smtp.UseImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(_smtp.UserName))
            {
                await client.AuthenticateAsync(_smtp.UserName, _smtp.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            LogSent(message.ToAddress, message.Subject);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Logged and swallowed, matching the contract on IEmailSender: an invitation whose
            // email bounced is still a created account, and rolling the account back would be
            // worse than a message the operator can resend. The subject is recorded, never the
            // body - it carries a single-use token.
            LogSendFailed(exception, message.ToAddress, message.Subject);
        }
    }

    [LoggerMessage(
        EventId = 2403,
        Level = LogLevel.Information,
        Message = "Email sent to {ToAddress}. Subject: {Subject}")]
    private partial void LogSent(string toAddress, string subject);

    [LoggerMessage(
        EventId = 2404,
        Level = LogLevel.Error,
        Message = "Email to {ToAddress} could not be sent. Subject: {Subject}")]
    private partial void LogSendFailed(Exception exception, string toAddress, string subject);
}
