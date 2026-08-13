using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Marketing.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ broker settings, bound from the <c>RabbitMQ</c> configuration section.
/// </summary>
/// <remarks>
/// Credentials should come from user secrets, environment variables, or the platform
/// secret store in deployed environments. The development defaults use RabbitMQ's
/// stock <c>guest/guest</c> credentials, which RabbitMQ restricts to loopback access.
/// </remarks>
public sealed class RabbitMqOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "RabbitMQ";

    /// <summary>
    /// Broker host name or IP address.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// AMQP port.
    /// </summary>
    [Range(1, 65_535)]
    public int Port { get; init; } = 5672;

    /// <summary>
    /// User the connection authenticates as.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Username { get; set; } = "guest";

    /// <summary>
    /// Password for <see cref="Username"/>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Password { get; set; } = "guest";

    /// <summary>
    /// Virtual host to connect to.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string VirtualHost { get; init; } = "/";

    /// <summary>
    /// RabbitMQ connection string.
    /// <para>
    /// When configured through <c>ConnectionStrings:RabbitMq</c>, this value
    /// overrides the value from the <c>RabbitMQ</c> section.
    /// </para>
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether messaging is enabled.
    /// <para>
    /// False registers no bus at all and falls back to a publisher that drops messages, so a
    /// deployment with no broker starts and serves traffic normally.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    [Range(1, 120)]
    public int ConnectionTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Requested AMQP heartbeat interval in seconds.
    /// </summary>
    [Range(1, 300)]
    public int RequestedHeartbeatSeconds { get; init; } = 60;

    // Connection and topology recovery are deliberately absent. MassTransit supervises the
    // connection itself — reconnecting, redeclaring topology and restarting receive endpoints — and
    // does not surface those as knobs. Keeping settings the transport cannot honour would be worse
    // than not having them: they read as configured and do nothing.

    /// <summary>
    /// Name reported to RabbitMQ for this application's connection, which is what makes a
    /// connection identifiable in the Management UI rather than showing as "undefined".
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ClientProvidedName { get; init; } = "Marketing.API";

    /// <summary>
    /// The broker endpoint without credentials.
    /// </summary>
    /// <remarks>
    /// Credentials are deliberately excluded so that logging this value does not
    /// expose the RabbitMQ password.
    /// </remarks>
    public string Endpoint =>
        $"amqp://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}{VirtualHost}";
}
