namespace Marketing.Scheduler.Abstractions;

/// <summary>
/// Declares a class as a scheduled job and carries its default schedule.
/// <para>
/// This attribute is the entire registration mechanism. A job that carries it is discovered by
/// assembly scanning at startup and wired into Quartz automatically - there is no central list to
/// edit, no dependency-injection call to remember, and therefore no way to write a job that
/// silently never runs because someone forgot the second step.
/// </para>
/// <para>
/// Everything here is a <em>default</em>. <c>Scheduler:Jobs:&lt;key&gt;</c> in configuration
/// overrides the cron expression and can disable the job entirely, so changing a schedule in
/// production is a config change rather than a deploy.
/// </para>
/// </summary>
/// <example>
/// Adding an email scheduler is one file:
/// <code>
/// [ScheduledJob(
///     Key = "email-dispatch",
///     Group = "email",
///     Cron = "0 */5 * * * ?",
///     Description = "Sends queued outbound email.")]
/// public sealed class EmailDispatchJob : ScheduledJobBase
/// {
///     private readonly IEmailQueueService _emails;
///
///     public EmailDispatchJob(IEmailQueueService emails, ILogger&lt;EmailDispatchJob&gt; logger)
///         : base(logger) => _emails = emails;
///
///     protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
///         _emails.DispatchPendingAsync(cancellationToken);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScheduledJobAttribute : Attribute
{
    /// <summary>
    /// Stable identifier for the job. Used as the Quartz job key and as the configuration key, so
    /// treat it as a contract: renaming it silently orphans any configuration override.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Quartz group, used to organise related jobs. Defaults to <c>general</c>.</summary>
    public string Group { get; init; } = "general";

    /// <summary>
    /// Quartz cron expression in six-or-seven field form (seconds first), interpreted as UTC.
    /// </summary>
    public required string Cron { get; init; }

    /// <summary>Operator-facing description, surfaced on the scheduler admin screen.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Whether the job is scheduled unless configuration says otherwise. Set false for jobs that
    /// should stay dormant until deliberately switched on.
    /// </summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>
    /// Whether a run may start while the previous one is still going.
    /// <para>
    /// Defaults to false. Overlapping runs of a job that drains a queue will process the same rows
    /// twice, which for an email or campaign dispatcher means sending a message twice.
    /// </para>
    /// </summary>
    public bool AllowConcurrentExecution { get; init; }
}
