using Marketing.Application.Services.WhatsApp;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Maintenance;

/// <summary>Removes header example files nobody attached to a template.</summary>
/// <remarks>
/// A file uploaded in the template editor and then abandoned - the editor closed, the template never
/// submitted - is a customer's image kept for no reason. A day is long enough for anyone still
/// working on a template.
/// </remarks>
[ScheduledJob(
    Key = "template-header-sample-cleanup",
    Group = "maintenance",
    Cron = "0 15 * * * ?",
    Description = "Removes template header example files not attached to a template within a day.")]
public sealed class TemplateHeaderSampleCleanupJob : ScheduledJobBase
{
    private readonly ITemplateHeaderSampleService _samples;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="samples">Header example files.</param>
    /// <param name="logger">Logger.</param>
    public TemplateHeaderSampleCleanupJob(ITemplateHeaderSampleService samples, ILogger<TemplateHeaderSampleCleanupJob> logger)
        : base(logger)
    {
        _samples = samples;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _samples.RemoveAbandonedAsync(cancellationToken);
}
