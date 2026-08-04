using Marketing.DataAccess.Configurations;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Interceptors;
using Marketing.DataAccess.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.DataAccess.Extensions;

/// <summary>Registers the data access layer.</summary>
public static class DataAccessServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ApplicationDbContext"/>, its interceptors and the seeder.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="isDevelopment">
    /// Whether the host is running in development. Gates sensitive-data logging and detailed
    /// errors, which must never be enabled elsewhere regardless of what configuration asks for.
    /// </param>
    public static IServiceCollection AddDataAccess(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<AuditingSaveChangesInterceptor>();
        services.AddScoped<AuditTrailInterceptor>();

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            return new SlowQueryLoggingInterceptor(
                provider.GetRequiredService<ILogger<SlowQueryLoggingInterceptor>>(),
                TimeSpan.FromMilliseconds(options.SlowQueryThresholdMilliseconds));
        });

        services.AddDbContext<ApplicationDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            builder.UseNpgsql(options.ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                npgsql.CommandTimeout(options.CommandTimeoutSeconds);

                // Retries transient PostgreSQL faults only - connection resets, failover during a
                // managed-service maintenance window. Constraint violations are not retried.
                npgsql.EnableRetryOnFailure(
                    options.MaxRetryCount,
                    TimeSpan.FromSeconds(options.MaxRetryDelaySeconds),
                    errorCodesToAdd: null);
            });

            // PostgreSQL identifiers are folded to lower case; snake_case keeps the schema readable
            // from psql without quoting every identifier.
            builder.UseSnakeCaseNamingConvention();

            // Order matters. Audit columns must be populated before the trail records them.
            builder.AddInterceptors(
                provider.GetRequiredService<AuditingSaveChangesInterceptor>(),
                provider.GetRequiredService<AuditTrailInterceptor>(),
                provider.GetRequiredService<SlowQueryLoggingInterceptor>());

            if (isDevelopment)
            {
                builder.EnableSensitiveDataLogging(options.EnableSensitiveDataLogging);
                builder.EnableDetailedErrors(options.EnableDetailedErrors);
            }
        });

        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}
