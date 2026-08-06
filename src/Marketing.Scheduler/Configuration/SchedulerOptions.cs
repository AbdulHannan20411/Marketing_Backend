using System.ComponentModel.DataAnnotations;

namespace Marketing.Scheduler.Configuration;

/// <summary>Scheduler settings, bound from the <c>Scheduler</c> configuration section.</summary>
public sealed class SchedulerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scheduler";

    /// <summary>
    /// Master switch. Turning the scheduler off leaves the API fully functional and is the fastest
    /// way to stop a misbehaving job in production without a deploy.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether to wait for in-flight jobs to finish before the host exits. Prevents a shutdown
    /// mid-batch from leaving partially applied work.
    /// </summary>
    public bool WaitForJobsToComplete { get; init; } = true;

    /// <summary>Maximum jobs Quartz may run concurrently.</summary>
    [Range(1, 200)]
    public int MaxConcurrency { get; init; } = 10;

    /// <summary>
    /// Per-job overrides, keyed by the job's <c>Key</c>. Absent entries fall back to the values on
    /// the job's <c>[ScheduledJob]</c> attribute.
    /// </summary>
    public Dictionary<string, JobOverride> Jobs { get; init; } = [];

    /// <summary>Configuration override for a single job.</summary>
    public sealed class JobOverride
    {
        /// <summary>Whether the job is scheduled. Overrides the attribute's default.</summary>
        public bool? Enabled { get; init; }

        /// <summary>Cron expression replacing the attribute's default. Interpreted as UTC.</summary>
        public string? Cron { get; init; }
    }
}
