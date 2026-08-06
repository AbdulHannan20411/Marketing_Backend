using Marketing.Scheduler.Configuration;
using Quartz;

namespace Marketing.Scheduler.Abstractions;

/// <summary>
/// A discovered job and its effective schedule, after configuration overrides have been applied to
/// the values declared on its <see cref="ScheduledJobAttribute"/>.
/// </summary>
/// <param name="JobType">The concrete <see cref="IJob"/> implementation.</param>
/// <param name="Key">Stable identifier, used for the Quartz key and the configuration key.</param>
/// <param name="Group">Quartz group.</param>
/// <param name="Cron">Effective cron expression.</param>
/// <param name="Description">Operator-facing description.</param>
/// <param name="Enabled">Whether the job is scheduled.</param>
public sealed record JobDescriptor(
    Type JobType,
    string Key,
    string Group,
    string Cron,
    string Description,
    bool Enabled)
{
    /// <summary>Quartz job key.</summary>
    public JobKey JobKey => new(Key, Group);

    /// <summary>Quartz trigger key.</summary>
    public TriggerKey TriggerKey => new($"{Key}-trigger", Group);

    /// <summary>
    /// Builds a descriptor from a job type's attribute and any matching configuration override.
    /// </summary>
    /// <param name="jobType">Concrete job type carrying a <see cref="ScheduledJobAttribute"/>.</param>
    /// <param name="options">Scheduler options supplying overrides.</param>
    /// <exception cref="InvalidOperationException">
    /// The type has no attribute, or the effective cron expression is not valid. Both are refused
    /// at startup rather than producing a job that silently never fires.
    /// </exception>
    public static JobDescriptor Create(Type jobType, SchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        ArgumentNullException.ThrowIfNull(options);

        var attribute = Attribute.GetCustomAttribute(jobType, typeof(ScheduledJobAttribute))
                            as ScheduledJobAttribute
                        ?? throw new InvalidOperationException(
                            $"{jobType.Name} is not decorated with [ScheduledJob].");

        options.Jobs.TryGetValue(attribute.Key, out var jobOverride);

        var cron = string.IsNullOrWhiteSpace(jobOverride?.Cron) ? attribute.Cron : jobOverride.Cron;

        if (!CronExpression.IsValidExpression(cron))
        {
            // A malformed cron would otherwise be accepted and the job would simply never run,
            // which is close to impossible to notice until someone asks why nothing happened.
            throw new InvalidOperationException(
                $"Job '{attribute.Key}' has an invalid cron expression: '{cron}'.");
        }

        return new JobDescriptor(
            jobType,
            attribute.Key,
            attribute.Group,
            cron,
            attribute.Description,
            jobOverride?.Enabled ?? attribute.EnabledByDefault);
    }
}
