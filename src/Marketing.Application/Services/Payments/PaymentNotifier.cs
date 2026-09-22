using Marketing.Application.Services.Email;
using System.Globalization;
using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Payments;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Payments;

/// <summary>
/// Tells everyone who needs to know that a payment was submitted or decided.
/// </summary>
/// <remarks>
/// Three channels for each event — an in-app notification, an email and a realtime push — behind
/// one call, so a new event cannot ship with two of the three wired.
/// <para>
/// Every method is best-effort and swallows its own failures. The decision is already committed by
/// the time these run; a bounced email or a dropped socket must not roll back a granted plan or
/// surface as a failed request to the reviewer.
/// </para>
/// </remarks>
public interface IPaymentNotifier
{
    /// <summary>Announces a new submission to the platform reviewers.</summary>
    /// <param name="request">The stored request.</param>
    /// <param name="response">Its described form, for the realtime payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SubmittedAsync(
        PaymentRequest request,
        PaymentRequestResponse response,
        CancellationToken cancellationToken = default);

    /// <summary>Tells the customer their payment was accepted and the plan granted.</summary>
    /// <param name="request">The stored request.</param>
    /// <param name="response">Its described form.</param>
    /// <param name="periodEnd">When the new period ends, for the email.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ApprovedAsync(
        PaymentRequest request,
        PaymentRequestResponse response,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken = default);

    /// <summary>Tells the customer their payment was refused, and why.</summary>
    /// <param name="request">The stored request.</param>
    /// <param name="response">Its described form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RejectedAsync(
        PaymentRequest request,
        PaymentRequestResponse response,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPaymentNotifier" />
public sealed partial class PaymentNotifier : IPaymentNotifier
{
    private readonly IRepository<Notification> _notifications;
    private readonly INotificationService _preferences;
    private readonly IUserRepository _users;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmailSender _email;
    private readonly IEmailTemplateRenderer _templates;
    private readonly IRealtimeNotifier _realtime;
    private readonly IDateTimeProvider _clock;
    private readonly EmailOptions _options;
    private readonly ILogger<PaymentNotifier> _logger;

    /// <summary>Initialises a new instance.</summary>
    public PaymentNotifier(
        IRepository<Notification> notifications,
        INotificationService preferences,
        IUserRepository users,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IEmailSender email,
        IEmailTemplateRenderer templates,
        IRealtimeNotifier realtime,
        IDateTimeProvider clock,
        IOptions<EmailOptions> options,
        ILogger<PaymentNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _notifications = notifications;
        _preferences = preferences;
        _users = users;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _email = email;
        _templates = templates;
        _realtime = realtime;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task SubmittedAsync(
        PaymentRequest request,
        PaymentRequestResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        return SafelyAsync(async () =>
        {
            var reviewers = await LoadReviewersAsync(cancellationToken);

            // A reviewer who switched billing notifications off is dropped before the row is
            // written, not hidden afterwards.
            var wanted = await _preferences.WhoWantsAsync(
                [.. reviewers.Select(reviewer => reviewer.Id)],
                NotificationKind.PaymentSubmitted,
                cancellationToken);

            var created = new List<Notification>(reviewers.Count);

            foreach (var reviewer in reviewers.Where(reviewer => wanted.Contains(reviewer.Id)))
            {
                // Addressed to the individual reviewer. A tenant-wide notification is not an option
                // here: platform staff sit outside every tenant.
                var notification = new Notification
                {
                    // Explicitly platform-level. The interceptor only fills a tenant in when one is
                    // absent, so this row keeps no workspace of its own - which is what makes it a
                    // reviewer's notification rather than a row filed inside the customer that
                    // happened to trigger it.
                    TenantId = null,
                    UserId = reviewer.Id,
                    Kind = NotificationKind.PaymentSubmitted,
                    Title = "Payment awaiting review",
                    Body = $"{request.Organisation} submitted "
                           + $"{Money(request)} for the {request.PlanName} plan.",
                    Priority = NotificationPriority.Warning,
                    Icon = "creditCard",
                    ActionLabel = "Review",
                    ActionRoute = "/superadmin/payments",
                    OccurredOn = _clock.UtcNow,
                };

                _notifications.Add(notification);
                created.Add(notification);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Pushed after the save, so the identifier the client receives is one it can mark read.
            foreach (var notification in created.Where(entry => entry.UserId is not null))
            {
                await _realtime.NotifyUserAsync(
                    notification.UserId!.Value, ToAppNotification(notification), cancellationToken);
            }

            foreach (var reviewer in reviewers)
            {
                await SendTemplateAsync(
                    "payments.submitted",
                    reviewer.Email,
                    reviewer.DisplayName,
                    Values(
                        ("name", reviewer.DisplayName),
                        ("organisation", request.Organisation),
                        ("planName", request.PlanName),
                        ("amount", Money(request)),
                        ("actionUrl", Link("/superadmin/payments"))),
                    cancellationToken);
            }

            await _realtime.PublishPaymentRequestAsync(request.TenantId, ToEvent(response), cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task ApprovedAsync(
        PaymentRequest request,
        PaymentRequestResponse response,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        return SafelyAsync(async () =>
        {
            await NotifyWorkspaceAsync(
                request,
                NotificationKind.PaymentApproved,
                "Payment approved",
                $"Your {request.PlanName} plan is active until {periodEnd:d MMMM yyyy}.",
                NotificationPriority.Success,
                "check-circle",
                cancellationToken);

            await SendTemplateAsync(
                "payments.approved",
                request.SubmittedByEmail,
                request.SubmittedByName,
                Values(
                    ("name", request.SubmittedByName),
                    ("amount", Money(request)),
                    ("planName", request.PlanName),
                    ("activeUntil", periodEnd.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
                    ("actionUrl", Link("/subscription"))),
                cancellationToken);

            await _realtime.PublishPaymentRequestAsync(request.TenantId, ToEvent(response), cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task RejectedAsync(
        PaymentRequest request,
        PaymentRequestResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        return SafelyAsync(async () =>
        {
            var reason = request.RejectionReason ?? string.Empty;

            await NotifyWorkspaceAsync(
                request,
                NotificationKind.PaymentRejected,
                "Payment could not be confirmed",
                reason,
                NotificationPriority.Critical,
                "alert-triangle",
                cancellationToken);

            // The reason travels verbatim. It is the only thing telling the customer what to fix,
            // so it is never summarised, truncated or replaced with a generic sentence.
            await SendTemplateAsync(
                "payments.rejected",
                request.SubmittedByEmail,
                request.SubmittedByName,
                Values(
                    ("name", request.SubmittedByName),
                    ("planName", request.PlanName),
                    ("reason", reason),
                    ("actionUrl", Link("/pricing"))),
                cancellationToken);

            await _realtime.PublishPaymentRequestAsync(request.TenantId, ToEvent(response), cancellationToken);
        });
    }

    /// <summary>Writes one notification visible to every member of the submitting workspace.</summary>
    private async Task NotifyWorkspaceAsync(
        PaymentRequest request,
        NotificationKind kind,
        string title,
        string body,
        NotificationPriority priority,
        string icon,
        CancellationToken cancellationToken)
    {
        var notification = new Notification
        {
            TenantId = request.TenantId,

            // Null recipient: everyone in the workspace sees it. A plan change affects the whole
            // team, not only whoever happened to upload the receipt.
            UserId = null,
            Kind = kind,
            Title = title,
            Body = body,
            Priority = priority,
            Icon = icon,
            ActionLabel = "View subscription",
            ActionRoute = "/subscription",
            OccurredOn = _clock.UtcNow,
        };

        _notifications.Add(notification);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Pushed as well as stored. Without this the row exists and the bell does not move until
        // something else causes a refresh, which is indistinguishable from no notification at all.
        if (request.TenantId is { } tenantId)
        {
            await _realtime.NotifyTenantAsync(
                tenantId, ToAppNotification(notification), cancellationToken);
        }
    }

    /// <summary>Projects a stored notification onto the shape the client renders.</summary>
    private static AppNotification ToAppNotification(Notification notification) =>
        new(
            PublicId.From(PublicId.Notification, notification.Id),
            notification.Kind,
            NotificationCategories.Of(notification.Kind),
            notification.Title,
            notification.Body,
            notification.Priority,
            notification.Icon,
            notification.Read,
            notification.ActionLabel,
            notification.ActionRoute,
            notification.OccurredOn);

    /// <summary>
    /// Platform administrators, who review payments.
    /// </summary>
    /// <remarks>
    /// Through the repository, which ignores the tenant filter. A submission runs inside the
    /// submitting tenant's scope, and platform staff have no tenant — so the obvious query returns
    /// nobody and the reviewers are never told.
    /// </remarks>
    private async Task<IReadOnlyList<ReviewerRow>> LoadReviewersAsync(CancellationToken cancellationToken)
    {
        var reviewers = await _users.GetPlatformAdministratorsAsync(cancellationToken);

        return [.. reviewers.Select(user => new ReviewerRow(user.Id, user.Email, user.DisplayName))];
    }

    private static PaymentRequestEvent ToEvent(PaymentRequestResponse response) =>
        new(
            response.Id,
            response.Status,
            response.PlanName,
            response.Organisation,
            response.RejectionReason);

    private async Task SendTemplateAsync(
        string key,
        string address,
        string name,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var message = await _templates.RenderAsync(key, address, name, values, cancellationToken);

        await _email.SendAsync(message, cancellationToken);
    }

    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => pair.Value ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// Runs the notification work, absorbing whatever it throws.
    /// </summary>
    /// <remarks>
    /// The decision is already committed when this runs. Letting a mail server refusing a
    /// connection surface as a 500 would tell the reviewer their approval failed when the customer
    /// has, in fact, been granted the plan.
    /// </remarks>
    private async Task SafelyAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
#pragma warning disable CA1031 // Notification delivery must never fail the operation behind it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogNotificationFailed(exception.GetType().Name);
        }
    }

    /// <summary>Builds a client link from configuration, never from a request header.</summary>
    private string Link(string path) => $"{_options.ClientBaseUrl.TrimEnd('/')}{path}";

    // Invariant, so the same amount reads the same in every email and notification whatever culture the
    // host runs under. A comma as the decimal separator in one message and a point in the next is the
    // sort of thing a customer screenshots.
    private static string Money(PaymentRequest request) =>
        string.Create(CultureInfo.InvariantCulture, $"{request.Currency} {request.Amount:N0}");

    [LoggerMessage(
        EventId = 2960,
        Level = LogLevel.Warning,
        Message = "Payment notification delivery failed with {ExceptionType}; the decision itself stands.")]
    private partial void LogNotificationFailed(string exceptionType);

    private sealed record ReviewerRow(long Id, string Email, string DisplayName);
}
