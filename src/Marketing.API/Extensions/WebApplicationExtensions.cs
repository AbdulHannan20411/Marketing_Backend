using Marketing.DataAccess.Configurations;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketing.API.Extensions;

/// <summary>Startup routines that need a built application.</summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Applies pending migrations and seeds baseline data, when configuration enables it.
    /// <para>
    /// Both are opt-in and intended for development and CI. In production, schema changes belong in
    /// a deployment step that runs once, not in application startup where every replica races to
    /// apply the same migration on the same database.
    /// </para>
    /// </summary>
    /// <param name="app">Application.</param>
    public static async Task<WebApplication> InitialiseDatabaseAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        if (!options.ApplyMigrationsOnStartup && !options.SeedOnStartup)
        {
            return app;
        }

        await using var scope = app.Services.CreateAsyncScope();

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(InitialiseDatabaseAsync));

        try
        {
            if (options.ApplyMigrationsOnStartup)
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                logger.LogInformation("Applying pending database migrations.");
                await context.Database.MigrateAsync();
            }

            if (options.SeedOnStartup)
            {
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

                await seeder.SeedAsync(
                    configuration["Bootstrap:AdministratorEmail"] ?? string.Empty,
                    configuration["Bootstrap:AdministratorPassword"] ?? string.Empty);
            }
        }
        catch (Exception exception)
        {
            // Rethrown deliberately. An instance that cannot reach a correctly migrated database
            // must fail to start rather than accept traffic it will fail to serve.
            logger.LogCritical(exception, "Database initialisation failed; the host will not start.");
            throw;
        }

        return app;
    }
}
