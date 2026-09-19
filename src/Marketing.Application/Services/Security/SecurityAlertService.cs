using System.Globalization;
using Marketing.Application.Configurations;
using Marketing.Application.Services.Email;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Security;

/// <summary>Tells the right people when an account's sign-ins look wrong.</summary>
public interface ISecurityAlertService
{
    /// <summary>Alerts on a sign-in that has been saved: a new device, a new place, sharing, high risk.</summary>
    /// <param name="user">Who signed in.</param>
    /// <param name="facts">What the sign-in turned out to be.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AnnounceSignInAsync(User user, SignInFacts facts, CancellationToken cancellationToken = default);

    /// <summary>Alerts once when wrong passwords reach the threshold. Adds rows; the caller saves.</summary>
    /// <param name="user">Account under attack, or misremembered.</param>
    /// <param name="recentFailures">Failures in the last fifteen minutes, including this one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task AnnounceFailedLoginsAsync(User user, int recentFailures, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISecurityAlertService" />
/// <remarks>
/// Three audiences, told different things. The person themselves gets "was this you?" with a way to
/// act. Their workspace's administrators get the facts about their own staff. Platform staff hear only
/// about high risk - and only they ever see the word "risk", because telling a customer their employee
/// is a suspected cheat on the strength of a score is how a platform loses the customer.
/// <para>
/// Every alert fires on a threshold being crossed, not on it being above: the fifth failed password,
/// the third displaced session. A condition that stays true would otherwise alert on every sign-in.
/// </para>
/// </remarks>
public sealed class SecurityAlertService : ISecurityAlertService
{
    /// <summary>Displacements in a day at which administrators are told the login may be shared.</summary>
    private static readonly int[] DisplacementThresholds = [3, 10];

    /// <summary>Wrong passwords in fifteen minutes at which administrators are told.</summary>
    private const int FailedLoginThreshold = 5;

    private readonly IRepository<Notification> _notifications;
    private readonly IRepository<SecurityEvent> _events;
    private readonly IRepository<Tenant> _tenants;
    private readonly IUserRepository _users;
    private readonly IEmailTemplateRenderer _templates;
    private readonly IEmailSender _email;
    private readonly IAccountRiskEvaluator _risk;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _clock;
    private readonly EmailOptions _emailOptions;
    private readonly AuthenticationPolicyOptions _policy;

    /// <summary>Initialises a new instance.</summary>
    public SecurityAlertService(
        IRepository<Notification> notifications,
        IRepository<SecurityEvent> events,
        IRepository<Tenant> tenants,
        IUserRepository users,
        IEmailTemplateRenderer templates,
        IEmailSender email,
        IAccountRiskEvaluator risk,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IDateTimeProvider clock,
        IOptions<EmailOptions> emailOptions,
        IOptions<AuthenticationPolicyOptions> policy)
    {
        ArgumentNullException.ThrowIfNull(emailOptions);
        ArgumentNullException.ThrowIfNull(policy);

        _notifications = notifications;
        _events = events;
        _tenants = tenants;
        _users = users;
        _templates = templates;
        _email = email;
        _risk = risk;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _emailOptions = emailOptions.Value;
        _policy = policy.Value;
    }

    /// <inheritdoc />
    public async Task AnnounceSignInAsync(User user, SignInFacts facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(facts);

        var where = Describe(facts);

        if (facts.IsNewDevice)
        {
            await EmailNewLoginAsync(user, facts, cancellationToken);

            Notify(
                user.TenantId,
                user.Id,
                NotificationKind.SecurityNewLogin,
                "New sign-in to your account",
                $"{where}. If this wasn't you, end that session and change your password.",
                NotificationPriority.Warning,
                "This wasn't me",
                "/account/security");

            await NotifyAdministratorsAsync(
                user,
                $"{user.DisplayName} signed in from a new device",
                where,
                NotificationPriority.Info,
                cancellationToken);

            if (facts.DevicesInWindow > _policy.MaxDevicesPerUser)
            {
                await NotifyAdministratorsAsync(
                    user,
                    $"{user.DisplayName} has used {Count(facts.DevicesInWindow)} devices",
                    $"That is more than the {Count(_policy.MaxDevicesPerUser)} allowed in "
                    + $"{Count(_policy.DeviceWindowDays)} days. Review their devices and end any you don't recognise.",
                    NotificationPriority.Warning,
                    cancellationToken);
            }
        }
        else if (facts.IsNewLocation)
        {
            await NotifyAdministratorsAsync(
                user,
                $"{user.DisplayName} signed in from {facts.Location}",
                where,
                NotificationPriority.Info,
                cancellationToken);
        }

        if (facts.DisplacedCount > 0)
        {
            var displacedToday = await CountEventsAsync(user.Id, SecurityEventKind.SessionDisplaced, TimeSpan.FromDays(1), cancellationToken);

            if (DisplacementThresholds.Contains(displacedToday))
            {
                await NotifyAdministratorsAsync(
                    user,
                    $"{user.DisplayName}'s account keeps signing in on different devices",
                    $"{Count(displacedToday)} times in 24 hours a new sign-in ended the one before it. "
                    + "The login may be shared between several people.",
                    NotificationPriority.Warning,
                    cancellationToken);
            }
        }

        await EscalateHighRiskAsync(user, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AnnounceFailedLoginsAsync(User user, int recentFailures, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (recentFailures != FailedLoginThreshold)
        {
            return;
        }

        await NotifyAdministratorsAsync(
            user,
            $"{Count(recentFailures)} failed sign-ins for {user.DisplayName}",
            "Several wrong passwords in 15 minutes. If they weren't trying to sign in, someone else may be.",
            NotificationPriority.Warning,
            cancellationToken);
    }

    /// <summary>Tells platform staff about an account that has become high risk, once a day at most.</summary>
    private async Task EscalateHighRiskAsync(User user, CancellationToken cancellationToken)
    {
        var assessment = await _risk.EvaluateAsync(user.Id, cancellationToken);

        if (assessment.Level != RiskLevel.High)
        {
            return;
        }

        if (await CountEventsAsync(user.Id, SecurityEventKind.HighRisk, TimeSpan.FromDays(1), cancellationToken) > 0)
        {
            return;
        }

        var now = _clock.UtcNow;
        var reasons = string.Join("; ", assessment.Reasons);

        _events.Add(new SecurityEvent
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Kind = SecurityEventKind.HighRisk,
            Detail = reasons.Length <= 500 ? reasons : reasons[..500],
            OccurredAt = now,
        });

        var organisation = await OrganisationNameAsync(user.TenantId, cancellationToken);

        foreach (var administrator in await _users.GetPlatformAdministratorsAsync(cancellationToken))
        {
            // Platform-level: no workspace of its own, so it lands with staff rather than inside the
            // customer that triggered it.
            Notify(
                null,
                administrator.Id,
                NotificationKind.SecurityAlert,
                $"High-risk account: {user.DisplayName} ({organisation})",
                reasons,
                NotificationPriority.Critical,
                "Investigate",
                user.TenantId is { } tenantId ? $"/superadmin/tenants/{Common.Helpers.PublicId.From(Common.Helpers.PublicId.Tenant, tenantId)}/security" : "/superadmin");
        }
    }

    /// <summary>Emails the person themselves, with a way to act if it wasn't them.</summary>
    private async Task EmailNewLoginAsync(User user, SignInFacts facts, CancellationToken cancellationToken)
    {
        var message = await _templates.RenderAsync(
            "security.new_login",
            user.Email,
            user.DisplayName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = user.DisplayName,
                ["device"] = facts.DeviceLabel,
                ["ipAddress"] = facts.IpAddress ?? "unknown",
                ["location"] = facts.Location ?? string.Empty,
                ["signedInAt"] = _clock.UtcNow.ToString("d MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture),
                ["actionUrl"] = $"{_emailOptions.ClientBaseUrl.TrimEnd('/')}/account/security",
            },
            cancellationToken);

        await _email.SendAsync(message, cancellationToken);
    }

    /// <summary>Tells the workspace's administrators about one of their people. Never the person themselves.</summary>
    private async Task NotifyAdministratorsAsync(
        User user,
        string title,
        string body,
        NotificationPriority priority,
        CancellationToken cancellationToken)
    {
        if (user.TenantId is not { } tenantId)
        {
            return;
        }

        foreach (var administrator in await _users.GetTenantAdministratorsAsync(tenantId, cancellationToken))
        {
            if (administrator.Id == user.Id)
            {
                continue;
            }

            Notify(tenantId, administrator.Id, NotificationKind.SecurityAlert, title, body, priority, "View devices", "/settings/security");
        }
    }

    private void Notify(
        long? tenantId,
        long userId,
        NotificationKind kind,
        string title,
        string body,
        NotificationPriority priority,
        string actionLabel,
        string actionRoute) =>
        _notifications.Add(new Notification
        {
            TenantId = tenantId,
            UserId = userId,
            Kind = kind,
            Title = title.Length <= 200 ? title : title[..200],
            Body = body,
            Priority = priority,
            Icon = "shield",
            ActionLabel = actionLabel,
            ActionRoute = actionRoute,
            OccurredOn = _clock.UtcNow,
        });

    private async Task<int> CountEventsAsync(
        long userId,
        SecurityEventKind kind,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var since = _clock.UtcNow - window;

        return await _queries.CountAsync(
            _events.Query()
                .IgnoreQueryFilters()
                .Where(securityEvent =>
                    !securityEvent.IsDeleted
                    && securityEvent.UserId == userId
                    && securityEvent.Kind == kind
                    && securityEvent.OccurredAt >= since),
            cancellationToken);
    }

    private async Task<string> OrganisationNameAsync(long? tenantId, CancellationToken cancellationToken)
    {
        if (tenantId is not { } id)
        {
            return "platform";
        }

        return await _queries.FirstOrDefaultAsync(
            _tenants.Query().IgnoreQueryFilters().Where(tenant => tenant.Id == id).Select(tenant => tenant.Name),
            cancellationToken) ?? "unknown workspace";
    }

    /// <summary>"Chrome / Windows in Lahore, PK · 39.45.12.8".</summary>
    private static string Describe(SignInFacts facts)
    {
        var place = facts.Location is { Length: > 0 } location ? $" in {location}" : string.Empty;
        var address = facts.IpAddress is { Length: > 0 } ip ? $" · {ip}" : string.Empty;

        return $"{facts.DeviceLabel}{place}{address}";
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
