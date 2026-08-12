using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.Messaging;

/// <summary>
/// Broker settings, bound from the <c>RabbitMQ</c> configuration section.
/// </summary>
/// <remarks>
/// Credentials belong in user secrets or the platform secret store, never in a checked-in
/// <c>appsettings.json</c>. The values in the development file are the broker's stock
/// <c>guest/guest</c> pair, which RabbitMQ itself refuses over any non-loopback connection — they
/// protect nothing and are deliberately worthless outside a developer machine.
/// </remarks>
public sealed class RabbitMqOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RabbitMQ";

    /// <summary>Broker host name or address.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>AMQP port.</summary>
    [Range(1, 65_535)]
    public int Port { get; init; } = 5672;

    /// <summary>User the connection authenticates as.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Username { get; init; } = "guest";

    /// <summary>Password for <see cref="Username"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Password { get; init; } = "guest";

    /// <summary>
    /// Virtual host to connect to. RabbitMQ's default is <c>/</c>, and a virtual host is the unit of
    /// isolation between applications sharing one broker.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string VirtualHost { get; init; } = "/";

    /// <summary>
    /// Whether to connect at all.
    /// <para>
    /// False leaves the settings bound and validated while nothing dials the broker, which is what
    /// lets a deployment without RabbitMQ start cleanly rather than failing on a dependency it has
    /// no work for.
    /// </para>
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The endpoint, for logs and health output.
    /// </summary>
    /// <remarks>
    /// Host, port and virtual host only. The credentials are deliberately absent: an AMQP URI
    /// carries the password inline, and anything built for display ends up in a log sooner or later.
    /// </remarks>
    public string Endpoint => $"amqp://{Host}:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}{VirtualHost}";
}
