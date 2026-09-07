using Marketing.Application.Services.Email;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Maintenance;

/// <summary>
/// Delivers queued transactional email.
/// <para>
/// The poller behind the outbox. Sending used to happen inside the request that caused it, which
/// meant creating an administrator held a database transaction open for a full SMTP conversation —
/// measured at over a second to Gmail before authentication even started — and a slow relay could
/// stall the request for the whole thirty-second timeout.
/// </para>
/// <para>
/// The job holds no delivery logic of its own: it is a trigger. Everything it does is reachable
/// from a service call, which is what makes the behaviour testable without a scheduler.
/// </para>
/// </summary>
[ScheduledJob(
    Key = "email-outbox",
    Group = "maintenance",
    // Every ten seconds. Somebody is usually waiting on the mail this sends — an invitation, a
    // password reset — so the interval is the delay they experience. It costs one indexed query
    // returning nothing on the overwhelming majority of ticks.
    Cron = "0/10 * * * * ?",
    Description = "Delivers queued transactional email.")]
public sealed class EmailOutboxJob : ScheduledJobBase
{
    private readonly IEmailOutboxProcessor _outbox;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="outbox">Outbox processor.</param>
    /// <param name="logger">Logger.</param>
    public EmailOutboxJob(IEmailOutboxProcessor outbox, ILogger<EmailOutboxJob> logger)
        : base(logger)
    {
        _outbox = outbox;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _outbox.SendDueAsync(cancellationToken);
}
