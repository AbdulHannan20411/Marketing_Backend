using Marketing.Common.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Marketing.Infrastructure.Logging;

/// <summary>Builds the Serilog pipeline.</summary>
public static class SerilogConfigurator
{
    /// <summary>
    /// Properties that must never appear in a log entry. Destructuring a request DTO or an
    /// exception's data bag can otherwise carry a password or a Meta access token straight into a
    /// rolling file that is shipped to a log aggregator.
    /// </summary>
    private static readonly string[] ForbiddenProperties =
    [
        "Password",
        "NewPassword",
        "CurrentPassword",
        "PasswordHash",
        "AccessToken",
        "RefreshToken",
        "TokenHash",
        "SigningKey",
        "ConnectionString",
        "Authorization",
        "ClientSecret",
        "SystemUserAccessToken",
    ];

    /// <summary>
    /// Configures sinks, enrichment and redaction.
    /// <para>
    /// Human-readable console output during development; compact JSON everywhere else, because
    /// structured events are what a log aggregator can actually query.
    /// </para>
    /// </summary>
    /// <param name="configuration">Logger configuration to populate.</param>
    /// <param name="hostConfiguration">Application configuration, read for overrides.</param>
    /// <param name="environment">Host environment.</param>
    public static void Configure(
        LoggerConfiguration configuration,
        IConfiguration hostConfiguration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostConfiguration);
        ArgumentNullException.ThrowIfNull(environment);

        configuration
            .ReadFrom.Configuration(hostConfiguration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "Marketing.API")
            .Enrich.With(new SensitiveDataRedactionEnricher(ForbiddenProperties));

        if (environment.IsDevelopment())
        {
            configuration.WriteTo.Console(
                outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}");
        }
        else
        {
            configuration.WriteTo.Console(new CompactJsonFormatter());
        }

        configuration.WriteTo.File(
            new CompactJsonFormatter(),
            path: Path.Combine(AppContext.BaseDirectory, "logs", "marketing-.json"),
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: 100L * 1024 * 1024,
            retainedFileCountLimit: 31,
            shared: true);
    }

    /// <summary>Correlation properties promoted onto every request-scoped log entry.</summary>
    public static void EnrichFromRequest(
        Serilog.IDiagnosticContext diagnosticContext,
        string correlationId,
        string? tenantSlug,
        Guid? userId)
    {
        ArgumentNullException.ThrowIfNull(diagnosticContext);

        diagnosticContext.Set(AppConstants.Headers.CorrelationId, correlationId);

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            // The slug, never the tenant id: log aggregators are widely readable inside an
            // organisation, and a readable label is more useful to an operator anyway.
            diagnosticContext.Set("TenantSlug", tenantSlug);
        }

        if (userId is { } id)
        {
            diagnosticContext.Set("UserId", id);
        }
    }
}
