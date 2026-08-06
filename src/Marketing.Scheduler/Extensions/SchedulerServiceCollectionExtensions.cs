using System.Reflection;
using Marketing.Scheduler.Abstractions;
using Marketing.Scheduler.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Marketing.Scheduler.Extensions;

/// <summary>Registers the scheduler and every job it can discover.</summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Quartz and schedules every class in this assembly that carries a
    /// <see cref="ScheduledJobAttribute"/>.
    /// <para>
    /// Discovery is by assembly scan on purpose. A central registration list is a second place to
    /// edit, and the failure mode when it is forgotten - a job that exists, compiles, and never
    /// runs - produces no error at all.
    /// </para>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="additionalAssemblies">
    /// Extra assemblies to scan, for jobs that live outside this project.
    /// </param>
    public static IServiceCollection AddScheduler(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] additionalAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SchedulerOptions>()
            .Bind(configuration.GetSection(SchedulerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var options = configuration.GetSection(SchedulerOptions.SectionName).Get<SchedulerOptions>()
                      ?? new SchedulerOptions();

        var descriptors = DiscoverJobs(options, additionalAssemblies);

        // Exposed so an admin endpoint can list what is scheduled and on what cadence without
        // reaching into Quartz's own metadata.
        services.AddSingleton<IReadOnlyList<JobDescriptor>>(descriptors);

        if (!options.Enabled)
        {
            // Descriptors are still registered so the admin screen can show the jobs as disabled
            // rather than the list simply appearing empty.
            return services;
        }

        services.AddQuartz(quartz =>
        {
            quartz.UseSimpleTypeLoader();
            quartz.UseInMemoryStore();
            quartz.UseDefaultThreadPool(pool => pool.MaxConcurrency = options.MaxConcurrency);

            foreach (var descriptor in descriptors.Where(job => job.Enabled))
            {
                quartz.AddJob(
                    descriptor.JobType,
                    descriptor.JobKey,
                    job => job
                        .WithIdentity(descriptor.JobKey)
                        .WithDescription(descriptor.Description)
                        .StoreDurably());

                quartz.AddTrigger(trigger => trigger
                    .ForJob(descriptor.JobKey)
                    .WithIdentity(descriptor.TriggerKey)
                    .WithCronSchedule(descriptor.Cron, cron => cron
                        // UTC everywhere. A schedule expressed in server local time silently
                        // shifts twice a year and differs between environments.
                        .InTimeZone(TimeZoneInfo.Utc)
                        // A run missed during a deploy executes once on restart, rather than
                        // firing every skipped occurrence in a burst.
                        .WithMisfireHandlingInstructionFireAndProceed()));
            }
        });

        services.AddQuartzHostedService(hosted =>
        {
            hosted.WaitForJobsToComplete = options.WaitForJobsToComplete;

            // Without this the scheduler starts before the host is ready to serve, so a job can
            // run against a half-initialised application on a cold start.
            hosted.AwaitApplicationStarted = true;
        });

        return services;
    }

    /// <summary>Scans for job types and turns them into descriptors.</summary>
    private static List<JobDescriptor> DiscoverJobs(SchedulerOptions options, Assembly[] additionalAssemblies)
    {
        var assemblies = new List<Assembly> { typeof(SchedulerServiceCollectionExtensions).Assembly };

        if (additionalAssemblies.Length > 0)
        {
            assemblies.AddRange(additionalAssemblies);
        }

        return [.. assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && typeof(IJob).IsAssignableFrom(type)
                           && Attribute.IsDefined(type, typeof(ScheduledJobAttribute)))
            .Select(type => JobDescriptor.Create(type, options))
            .OrderBy(descriptor => descriptor.Group, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Key, StringComparer.Ordinal)];
    }
}
