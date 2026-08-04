using Serilog.Core;
using Serilog.Events;

namespace Marketing.Infrastructure.Logging;

/// <summary>
/// Replaces the value of any log property whose name matches a forbidden term.
/// <para>
/// A last line of defence rather than the first. The first is not putting secrets in log messages;
/// this catches the cases that slip through - a destructured request object, a rethrown exception
/// carrying a connection string - where the alternative is a credential sitting in a retained file.
/// </para>
/// </summary>
public sealed class SensitiveDataRedactionEnricher : ILogEventEnricher
{
    private const string Placeholder = "[redacted]";

    private readonly string[] _forbiddenProperties;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="forbiddenProperties">Property-name fragments to redact, matched case-insensitively.</param>
    public SensitiveDataRedactionEnricher(string[] forbiddenProperties)
    {
        _forbiddenProperties = forbiddenProperties;
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var property in logEvent.Properties)
        {
            if (!IsForbidden(property.Key))
            {
                continue;
            }

            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty(property.Key, Placeholder));
        }
    }

    private bool IsForbidden(string propertyName)
    {
        foreach (var forbidden in _forbiddenProperties)
        {
            if (propertyName.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
