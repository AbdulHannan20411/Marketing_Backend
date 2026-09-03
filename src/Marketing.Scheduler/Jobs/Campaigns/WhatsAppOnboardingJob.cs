using Marketing.Application.Services;
using Marketing.Scheduler.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Scheduler.Jobs.Campaigns;

/// <summary>
/// Finishes connecting WhatsApp accounts that are part-way through onboarding.
/// <para>
/// Connecting takes three round trips to Meta — subscribing to webhooks, registering the number and
/// reading the profile back — each of which can take seconds and fail for its own reason. Running
/// them inside the request that started the connection gave the administrator one opaque answer
/// several seconds later, and none at all if the browser gave up first.
/// </para>
/// <para>
/// The job holds no onboarding logic of its own: it is a trigger. Everything it does is reachable
/// from a service call, which is what makes the behaviour testable without a scheduler.
/// </para>
/// </summary>
[ScheduledJob(
    Key = "whatsapp-onboarding",
    Group = "campaigns",
    // Every five seconds. An administrator is watching a progress panel while this runs, so the
    // interval is the delay between one step lighting up and the next - slower reads as a hang.
    // It costs one indexed query returning nothing on the overwhelming majority of ticks.
    Cron = "0/5 * * * * ?",
    Description = "Advances WhatsApp connections still completing onboarding.")]
public sealed class WhatsAppOnboardingJob : ScheduledJobBase
{
    private readonly IWhatsAppConnectionService _connections;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="connections">Connection service.</param>
    /// <param name="logger">Logger.</param>
    public WhatsAppOnboardingJob(IWhatsAppConnectionService connections, ILogger<WhatsAppOnboardingJob> logger)
        : base(logger)
    {
        _connections = connections;
    }

    /// <inheritdoc />
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) =>
        _connections.RunPendingOnboardingAsync(cancellationToken);
}
