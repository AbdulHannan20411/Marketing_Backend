using System.ComponentModel.DataAnnotations;

namespace Marketing.Application.Configurations;

/// <summary>
/// SMTP settings, bound from the <c>Smtp</c> configuration section.
/// <para>
/// Delivery is enabled by the presence of <see cref="Host"/> and nothing else. Leave it empty and
/// the platform keeps recording messages instead of sending them, which is what every environment
/// without a mail relay should do.
/// </para>
/// </summary>
public sealed class SmtpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Smtp";

    /// <summary>Relay host name. Empty disables delivery.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Relay port. 587 is submission with STARTTLS, which is what Gmail and most providers expect;
    /// 465 is implicit TLS.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    /// <summary>
    /// Whether the connection is TLS from the first byte, as on port 465.
    /// <para>
    /// False means connect in the clear and upgrade with STARTTLS, which is the correct choice on
    /// 587. It never means "send unencrypted": MailKit still requires the upgrade to succeed.
    /// </para>
    /// </summary>
    public bool UseImplicitTls { get; init; }

    /// <summary>Account the relay authenticates. For Gmail this is the full address.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// Relay password. For Gmail this is a 16-character App Password, never the account password.
    /// <para>
    /// Supplied from user secrets or the platform secret store. It must never be committed: it
    /// grants the ability to send as this address.
    /// </para>
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>How long to wait for the relay before giving up.</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Whether enough is configured to attempt delivery.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
