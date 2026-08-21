using Marketing.Application.Services.Billing;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Billing;

/// <summary>
/// Warns every customer whose subscription lapses within a week, once a day.
/// <para>
/// Daily rather than hourly. The unit the customer is told about is days, so running more often
/// would produce no new information and several identical emails; the idempotency marker on the
/// subscription would suppress the duplicates anyway, making the extra runs pure cost.
/// </para>
/// <para>
/// Early morning UTC so the message is waiting when a working day starts across most of the world,
/// and off the hour so it does not contend with everything else scheduled at midnight.
/// </para>
/// </summary>
[ScheduledJob(
    Key = "subscription-expiry-reminders",
    Group = "billing",
    Cron = "0 30 6 * * ?",
    Description = "Emails and notifies administrators whose subscription expires within seven days.")]
public sealed class SubscriptionExpiryReminderJob : ScheduledJobBase
{
    private readonly ISubscriptionExpiryReminderService _reminders;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="reminders">Reminder service.</param>
    /// <param name="logger">Logger.</param>
    public SubscriptionExpiryReminderJob(
        ISubscriptionExpiryReminderService reminders,
        ILogger<SubscriptionExpiryReminderJob> logger)
        : base(logger)
    {
        _reminders = reminders;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _reminders.SendDueRemindersAsync(cancellationToken);
}
