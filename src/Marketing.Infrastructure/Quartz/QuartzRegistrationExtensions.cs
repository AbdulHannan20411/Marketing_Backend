using Marketing.Infrastructure.Quartz.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Marketing.Infrastructure.Quartz;

/// <summary>Registers the background scheduler and its jobs.</summary>
public static class QuartzRegistrationExtensions
{
    /// <summary>Adds Quartz with dependency-injected jobs and the maintenance schedule.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddQuartz(quartz =>
        {
            // Scoped job factory: each execution gets its own DbContext and its own tenant scope,
            // exactly like a request. Without it a job would capture a single context for the
            // process lifetime and accumulate tracked entities indefinitely.
            quartz.UseSimpleTypeLoader();
            quartz.UseInMemoryStore();

            quartz.AddJob<RefreshTokenCleanupJob>(job => job
                .WithIdentity(RefreshTokenCleanupJob.Key)
                .WithDescription("Deletes refresh tokens past their retention window."));

            quartz.AddTrigger(trigger => trigger
                .ForJob(RefreshTokenCleanupJob.Key)
                .WithIdentity("refresh-token-cleanup-trigger", "maintenance")
                // 03:15 UTC daily: outside the traffic peak, and deliberately not on the hour where
                // it would contend with every other system's scheduled work.
                .WithCronSchedule("0 15 3 * * ?", cron => cron
                    .InTimeZone(TimeZoneInfo.Utc)
                    // A missed run - a deploy spanning the window - executes once on restart rather
                    // than firing every skipped occurrence in a burst.
                    .WithMisfireHandlingInstructionFireAndProceed()));
        });

        services.AddQuartzHostedService(options =>
        {
            // Let in-flight jobs finish before the host exits, so a shutdown mid-purge does not
            // leave a partially applied batch.
            options.WaitForJobsToComplete = true;
        });

        return services;
    }
}
