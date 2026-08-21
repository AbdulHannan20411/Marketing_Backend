using Marketing.Application.Configurations;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Billing;

/// <summary>Warns customers whose subscription is about to lapse.</summary>
public interface ISubscriptionExpiryReminderService
{
    /// <summary>
    /// Sends any reminder that has become due, across every tenant.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many reminders were sent.</returns>
    public Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISubscriptionExpiryReminderService" />
/// <remarks>
/// One reminder a day for the last seven days, each carrying the days remaining. The countdown is
/// the point: a single warning a week out is read and forgotten, and a single warning on the last
/// day arrives after the person who could act on it has left for the weekend.
/// </remarks>
public sealed partial class SubscriptionExpiryReminderService : ISubscriptionExpiryReminderService
{
    /// <summary>How far ahead to start warning.</summary>
    private const int FirstReminderDay = 7;

    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<Notification> _notifications;
    private readonly IUserRepository _users;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailSender _email;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly EmailOptions _options;
    private readonly ILogger<SubscriptionExpiryReminderService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public SubscriptionExpiryReminderService(
        IRepository<TenantSubscription> subscriptions,
        IRepository<Notification> notifications,
        IUserRepository users,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IEmailSender email,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        IOptions<EmailOptions> options,
        ILogger<SubscriptionExpiryReminderService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _subscriptions = subscriptions;
        _notifications = notifications;
        _users = users;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _email = email;
        _tenantContext = tenantContext;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var horizon = now.AddDays(FirstReminderDay + 1);

        var due = await _queries.ToListAsync(
            _subscriptions.Query(asNoTracking: false)
                // No principal on a scheduled job, so the ambient tenant is null and the filter
                // would match nothing. Each subscription carries its own tenant, which is entered
                // explicitly before anything is written.
                .IgnoreQueryFilters()
                .Where(subscription =>
                    !subscription.IsDeleted
                    && subscription.TenantId != null
                    && subscription.ExpiresAt > now
                    && subscription.ExpiresAt <= horizon

                    // A lapsed or cancelled subscription has nothing to warn about, and a plan set
                    // to renew itself is not about to lapse.
                    && !subscription.AutoRenew
                    && (subscription.Status == SubscriptionStatus.Active
                        || subscription.Status == SubscriptionStatus.Trial))
                .Include(subscription => subscription.SubscriptionPlan),
            cancellationToken);

        var sent = 0;

        foreach (var subscription in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var daysRemaining = DaysUntil(subscription.ExpiresAt, now);

            if (!IsDue(subscription, daysRemaining))
            {
                continue;
            }

            try
            {
                await RemindAsync(subscription, daysRemaining, cancellationToken);
                sent++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One tenant's bad address must not stop every other tenant's warning. The marker
                // is only advanced on success, so this subscription is retried on the next run.
                LogReminderFailed(exception, subscription.Id, daysRemaining);
            }
        }

        return sent;
    }

    /// <summary>
    /// Whole days between today and the expiry date, counted as calendar days.
    /// </summary>
    /// <remarks>
    /// Calendar days, not elapsed hours. A subscription expiring at 23:00 tomorrow is "1 day left"
    /// to the person reading the email, whatever a subtraction of instants would round to.
    /// </remarks>
    private static int DaysUntil(DateTimeOffset expiresAt, DateTimeOffset now) =>
        (expiresAt.UtcDateTime.Date - now.UtcDateTime.Date).Days;

    /// <summary>Whether this subscription has crossed a threshold it has not been warned about.</summary>
    /// <remarks>
    /// Strictly-lower rather than not-equal. If the job does not run for two days - a deploy, an
    /// outage - the countdown skips from 5 to 3 and the customer still gets the 3-day warning
    /// rather than nothing at all, because 3 is below the 5 already recorded.
    /// </remarks>
    private static bool IsDue(TenantSubscription subscription, int daysRemaining) =>
        daysRemaining is >= 1 and <= FirstReminderDay
        && (subscription.LastExpiryReminderDay is null
            || daysRemaining < subscription.LastExpiryReminderDay);

    private async Task RemindAsync(
        TenantSubscription subscription,
        int daysRemaining,
        CancellationToken cancellationToken)
    {
        var tenantId = subscription.TenantId!.Value;
        var planName = subscription.SubscriptionPlan?.Name ?? "subscription";
        var expiresOn = subscription.ExpiresAt;
        var wording = daysRemaining == 1 ? "tomorrow" : $"in {daysRemaining} days";

        // Entered so the notification is written against the right tenant: the auditing interceptor
        // stamps TenantId from this context, not from the entity handed to it.
        using (_tenantContext.BeginScope(tenantId))
        {
            _notifications.Add(new Notification
            {
                TenantId = tenantId,

                // Null recipient: everyone in the workspace sees it. Billing lapsing affects the
                // whole team, and addressing only one person means it is missed while they are away.
                UserId = null,
                Kind = NotificationKind.SubscriptionExpiring,
                Title = $"Your {planName} plan expires {wording}",
                Body = $"Renew before {expiresOn:d MMMM yyyy} to keep sending campaigns.",

                // Escalating: a week out is information, the last two days are not.
                Priority = daysRemaining <= 2 ? NotificationPriority.Critical : NotificationPriority.Warning,
                Icon = "clock",
                ActionLabel = "Renew",
                ActionRoute = "/subscription",
            });

            // Recorded before the emails go out, and committed with them in the same unit of work.
            // Advancing it afterwards would re-send the whole batch if the process died in between.
            subscription.LastExpiryReminderDay = daysRemaining;

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var administrators = await _users.GetTenantAdministratorsAsync(tenantId, cancellationToken);

        foreach (var administrator in administrators)
        {
            await SendEmailAsync(administrator, planName, expiresOn, daysRemaining, wording, cancellationToken);
        }

        LogReminderSent(subscription.Id, daysRemaining, administrators.Count);
    }

    private async Task SendEmailAsync(
        User administrator,
        string planName,
        DateTimeOffset expiresOn,
        int daysRemaining,
        string wording,
        CancellationToken cancellationToken)
    {
        var link = $"{_options.ClientBaseUrl.TrimEnd('/')}/subscription";
        var name = Escape(administrator.DisplayName);
        var plan = Escape(planName);

        await _email.SendAsync(
            new EmailMessage(
                administrator.Email,
                administrator.DisplayName,
                $"Your {planName} plan expires {wording}",
                $"<p>Hello {name},</p>"
                + $"<p>Your <strong>{plan}</strong> plan expires {wording}, on "
                + $"{expiresOn:d MMMM yyyy}.</p>"
                + "<p>After that, campaigns stop sending until the plan is renewed.</p>"
                + $"<p><a href=\"{link}\">Renew your plan</a></p>",
                $"Hello {administrator.DisplayName},\n\n"
                + $"Your {planName} plan expires {wording}, on {expiresOn:d MMMM yyyy}. "
                + "After that, campaigns stop sending until the plan is renewed.\n\n"
                + $"Renew at {link}"),
            cancellationToken);
    }

    /// <summary>Escapes a value that reaches the reader as HTML.</summary>
    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Sent expiry reminder for subscription {SubscriptionId}: {DaysRemaining} days "
                  + "remaining, {RecipientCount} administrators emailed.")]
    private partial void LogReminderSent(long subscriptionId, int daysRemaining, int recipientCount);

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Error,
        Message = "Expiry reminder for subscription {SubscriptionId} at {DaysRemaining} days failed.")]
    private partial void LogReminderFailed(Exception exception, long subscriptionId, int daysRemaining);
}
