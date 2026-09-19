using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Security;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Security;

/// <summary>Who is signed in where, for the three people entitled to ask.</summary>
public interface ISecurityOverviewService
{
    /// <summary>A workspace's seats against its sessions and devices.</summary>
    /// <param name="tenantId">Workspace to read.</param>
    /// <param name="includeRisk">True for platform staff only; a workspace never sees risk about its own people.</param>
    /// <param name="viewerUserId">Who is looking, so their own row is not offered for suspension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<OrganizationSecurityResponse> GetOrganizationAsync(
        long tenantId,
        bool includeRisk,
        long? viewerUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends an account: sign-in refused and every session ended, in one transaction.
    /// </summary>
    /// <param name="tenantId">Workspace the account must belong to.</param>
    /// <param name="userId">The account.</param>
    /// <param name="request">Reason, and the level the screen showed.</param>
    /// <param name="byPlatformStaff">True from the platform route, which may also suspend the workspace's admin.</param>
    /// <param name="actorUserId">Who is suspending.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account's row, as the overview shows it.</returns>
    public Task<EmployeeSecurityResponse> SuspendAsync(
        long tenantId,
        long userId,
        SuspendAccountRequest request,
        bool byPlatformStaff,
        long actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Lets a suspended account sign in again. Sessions do not come back.</summary>
    /// <param name="tenantId">Workspace the account must belong to.</param>
    /// <param name="userId">The account.</param>
    /// <param name="byPlatformStaff">True from the platform route.</param>
    /// <param name="actorUserId">Who is reactivating.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<EmployeeSecurityResponse> ReactivateAsync(
        long tenantId,
        long userId,
        bool byPlatformStaff,
        long actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>The devices one account has used within the window.</summary>
    /// <param name="tenantId">Workspace the account must belong to, or null when the caller is the account itself.</param>
    /// <param name="userId">Account to read.</param>
    /// <param name="currentSessionId">The caller's own session, marked so it is not ended by mistake.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(
        long? tenantId,
        long userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Ends one session.</summary>
    /// <param name="sessionId">Public session identifier.</param>
    /// <param name="tenantId">Workspace it must belong to, or null for platform staff.</param>
    /// <param name="ownerUserId">Account it must belong to, when a person is ending their own.</param>
    /// <param name="revokedByUserId">Who is ending it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RevokeAsync(
        string sessionId,
        long? tenantId,
        long? ownerUserId,
        long revokedByUserId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISecurityOverviewService" />
/// <remarks>
/// Reads across tenants and applies the boundary itself: platform staff see every workspace by design,
/// and an administrator's own tenant is passed in explicitly, so a mistaken id belonging to another
/// workspace is not found rather than shown.
/// </remarks>
public sealed class SecurityOverviewService : ISecurityOverviewService
{
    private readonly IRepository<UserSession> _sessions;
    private readonly IRepository<SecurityEvent> _events;
    private readonly IRepository<Tenant> _tenants;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IUserRepository _users;
    private readonly ISessionTracker _tracker;
    private readonly IAccountRiskEvaluator _risk;
    private readonly IQueryExecutor _queries;
    private readonly IDateTimeProvider _clock;
    private readonly AuthenticationPolicyOptions _policy;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditLogRepository _audit;
    private readonly IRepository<Notification> _notifications;
    private readonly IRequestContext _requestContext;

    /// <summary>Initialises a new instance.</summary>
    public SecurityOverviewService(
        IRepository<UserSession> sessions,
        IRepository<SecurityEvent> events,
        IRepository<Tenant> tenants,
        IRepository<TenantSubscription> subscriptions,
        IUserRepository users,
        ISessionTracker tracker,
        IAccountRiskEvaluator risk,
        IQueryExecutor queries,
        IDateTimeProvider clock,
        IOptions<AuthenticationPolicyOptions> policy,
        IUnitOfWork unitOfWork,
        IAuditLogRepository audit,
        IRepository<Notification> notifications,
        IRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _unitOfWork = unitOfWork;
        _audit = audit;
        _notifications = notifications;
        _requestContext = requestContext;
        _sessions = sessions;
        _events = events;
        _tenants = tenants;
        _subscriptions = subscriptions;
        _users = users;
        _tracker = tracker;
        _risk = risk;
        _queries = queries;
        _clock = clock;
        _policy = policy.Value;
    }

    /// <inheritdoc />
    public async Task<OrganizationSecurityResponse> GetOrganizationAsync(
        long tenantId,
        bool includeRisk,
        long? viewerUserId = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var windowStart = now.AddDays(-_policy.DeviceWindowDays);
        var activeSince = now.AddMinutes(-_policy.ActiveWindowMinutes);
        var dayAgo = now.AddDays(-1);

        var tenant = await _queries.FirstOrDefaultAsync(
            _tenants.Query().IgnoreQueryFilters().Where(candidate => candidate.Id == tenantId && !candidate.IsDeleted)
                .Select(candidate => new { candidate.Name }),
            cancellationToken)
            ?? throw new NotFoundException("Organization", PublicId.From(PublicId.Tenant, tenantId));

        var subscription = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query().IgnoreQueryFilters()
                .Where(candidate => !candidate.IsDeleted && candidate.TenantId == tenantId)
                .Select(candidate => new
                {
                    PlanName = candidate.SubscriptionPlan.Name,
                    candidate.SeatsPurchased,
                    candidate.SubscriptionPlan.MaxEmployees,
                }),
            cancellationToken);

        var people = await _queries.ToListAsync(
            _users.Query().IgnoreQueryFilters()
                .Where(user => !user.IsDeleted && user.TenantId == tenantId)
                .Select(user => new
                {
                    user.Id,
                    user.DisplayName,
                    user.Email,
                    user.Status,
                    Role = user.UserRoles.Where(userRole => !userRole.IsDeleted).Select(userRole => userRole.Role.Name).FirstOrDefault(),
                    IsPlatformStaff = user.UserRoles.Any(userRole =>
                        !userRole.IsDeleted && userRole.Role.Name == Common.Constants.Roles.SuperAdmin),
                    IsAdmin = user.UserRoles.Any(userRole =>
                        !userRole.IsDeleted && userRole.Role.Name == Common.Constants.Roles.Admin),
                }),
            cancellationToken);

        // Platform staff never appear here, whatever row a data fix may have left carrying a tenant.
        people = [.. people.Where(person => !person.IsPlatformStaff)];

        var sessions = await _queries.ToListAsync(
            _sessions.Query().IgnoreQueryFilters()
                .Where(session => !session.IsDeleted && session.TenantId == tenantId && session.LastActivityAt >= windowStart)
                .Select(session => new { session.UserId, session.DeviceId, session.LastActivityAt, session.RevokedAt }),
            cancellationToken);

        var displaced = await _queries.ToListAsync(
            _events.Query().IgnoreQueryFilters()
                .Where(securityEvent =>
                    !securityEvent.IsDeleted
                    && securityEvent.TenantId == tenantId
                    && securityEvent.Kind == SecurityEventKind.SessionDisplaced
                    && securityEvent.OccurredAt >= dayAgo)
                .Select(securityEvent => securityEvent.UserId),
            cancellationToken);

        var employees = new List<EmployeeSecurityResponse>(people.Count);

        foreach (var person in people)
        {
            var own = sessions.Where(session => session.UserId == person.Id).ToList();

            RiskResponse? risk = null;

            if (includeRisk)
            {
                var assessment = await _risk.EvaluateAsync(person.Id, cancellationToken);

                risk = new RiskResponse(assessment.Level, assessment.Score, assessment.Reasons);
            }

            employees.Add(new EmployeeSecurityResponse(
                PublicId.From(PublicId.Employee, person.Id),
                person.DisplayName,
                person.Email,
                person.Role ?? string.Empty,
                own.Count(session => session.RevokedAt == null && session.LastActivityAt >= activeSince),
                own.Select(session => session.DeviceId).Distinct(StringComparer.Ordinal).Count(),
                own.Count > 0 ? own.Max(session => session.LastActivityAt) : null,
                displaced.Count(userId => userId == person.Id),
                risk,
                ToEmployeeStatus(person.Status),
                CanSuspend(person.Id, viewerUserId, person.IsPlatformStaff, person.IsAdmin, byPlatformStaff: includeRisk)));
        }

        // Riskiest first for staff, who are here to find the problem; alphabetical for a workspace,
        // which is here to find a person.
        var ordered = includeRisk
            ? employees.OrderByDescending(employee => employee.Risk?.Score ?? 0).ThenBy(employee => employee.Name, StringComparer.OrdinalIgnoreCase)
            : employees.OrderBy(employee => employee.Name, StringComparer.OrdinalIgnoreCase);

        var purchased = subscription is null
            ? null
            : subscription.SeatsPurchased > 0 ? subscription.SeatsPurchased : subscription.MaxEmployees;

        return new OrganizationSecurityResponse(
            PublicId.From(PublicId.Tenant, tenantId),
            tenant.Name,
            subscription?.PlanName ?? "No plan",
            purchased,
            people.Count(person => person.Status == Common.Constants.AppConstants.UserStatus.Active),
            sessions.Count(session => session.RevokedAt == null && session.LastActivityAt >= activeSince),
            sessions.Select(session => session.DeviceId).Distinct(StringComparer.Ordinal).Count(),
            _policy.DeviceWindowDays,
            [.. ordered]);
    }

    /// <inheritdoc />
    public async Task<EmployeeSecurityResponse> SuspendAsync(
        long tenantId,
        long userId,
        SuspendAccountRequest request,
        bool byPlatformStaff,
        long actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reason = request.Reason?.Trim() is { Length: > 0 } written ? written : null;

        if (reason is { Length: > 500 })
        {
            throw new ValidationException("reason", "Keep the reason to 500 characters or fewer.");
        }

        var alertLevel = request.AlertLevel?.Trim().ToLowerInvariant() is "low" or "warning" or "high"
            ? request.AlertLevel.Trim().ToLowerInvariant()
            : null;

        var target = await LoadTargetAsync(tenantId, userId, byPlatformStaff, actorUserId, cancellationToken);

        if (target.Status != Common.Constants.AppConstants.UserStatus.Disabled)
        {
            // The server's own view, recorded next to what the screen showed: the level the button
            // was pressed at is a claim from the browser, this is the evidence.
            var assessment = await _risk.EvaluateAsync(target.Id, cancellationToken);

            await _unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    target.Status = Common.Constants.AppConstants.UserStatus.Disabled;

                    // Any access token still in flight is refused at its next refresh as well.
                    target.SecurityStamp = Guid.NewGuid();

                    await _tracker.RevokeAllAsync(target.Id, "Account suspended.", actorUserId, token);

                    Audit(tenantId, actorUserId, target.Id, "security.account.suspended", new
                    {
                        actor = actorUserId,
                        target = PublicId.From(PublicId.Employee, target.Id),
                        alertLevel,
                        riskLevel = assessment.Level.ToString().ToLowerInvariant(),
                        riskScore = assessment.Score,
                        reason,
                        byPlatformStaff,
                    });

                    if (byPlatformStaff)
                    {
                        await NotifyWorkspaceAdminsAsync(tenantId, target, token);
                    }

                    await _unitOfWork.SaveChangesAsync(token);
                },
                cancellationToken);
        }

        return await RowAsync(tenantId, target.Id, byPlatformStaff, actorUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmployeeSecurityResponse> ReactivateAsync(
        long tenantId,
        long userId,
        bool byPlatformStaff,
        long actorUserId,
        CancellationToken cancellationToken = default)
    {
        var target = await LoadTargetAsync(tenantId, userId, byPlatformStaff, actorUserId, cancellationToken);

        // Only an account suspended is reactivated here. An invited one stays invited: this must not
        // become a way to skip accepting an invitation.
        if (target.Status == Common.Constants.AppConstants.UserStatus.Disabled)
        {
            target.Status = Common.Constants.AppConstants.UserStatus.Active;

            Audit(tenantId, actorUserId, target.Id, "security.account.reactivated", new
            {
                actor = actorUserId,
                target = PublicId.From(PublicId.Employee, target.Id),
                byPlatformStaff,
            });

            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await RowAsync(tenantId, target.Id, byPlatformStaff, actorUserId, cancellationToken);
    }

    /// <summary>Who may be suspended from a given screen, by whom.</summary>
    private static bool CanSuspend(long target, long? viewer, bool isPlatformStaff, bool isAdmin, bool byPlatformStaff) =>
        target != viewer && !isPlatformStaff && (byPlatformStaff || !isAdmin);

    private static EmployeeStatus ToEmployeeStatus(Common.Constants.AppConstants.UserStatus status) => status switch
    {
        Common.Constants.AppConstants.UserStatus.Active => EmployeeStatus.Active,
        Common.Constants.AppConstants.UserStatus.Invited => EmployeeStatus.Invited,
        _ => EmployeeStatus.Suspended,
    };

    /// <summary>
    /// Loads the account to act on, refusing anyone the caller may not suspend.
    /// </summary>
    /// <remarks>
    /// Platform staff are refused before the workspace check, so the answer is the same on every
    /// route. Otherwise the workspace boundary is applied here: an id from another workspace is not
    /// found, exactly as it is on the overview.
    /// </remarks>
    private async Task<User> LoadTargetAsync(
        long tenantId,
        long userId,
        bool byPlatformStaff,
        long actorUserId,
        CancellationToken cancellationToken)
    {
        if (userId == actorUserId)
        {
            throw new ForbiddenException("cannot_suspend_self", "You can't suspend your own account.");
        }

        var target = await _queries.FirstOrDefaultAsync(
            _users.Query(asNoTracking: false).IgnoreQueryFilters()
                .Include(user => user.UserRoles).ThenInclude(userRole => userRole.Role)
                .Where(user => user.Id == userId && !user.IsDeleted),
            cancellationToken)
            ?? throw new NotFoundException("Employee", PublicId.From(PublicId.Employee, userId));

        var roles = target.UserRoles.Where(userRole => !userRole.IsDeleted).Select(userRole => userRole.Role.Name).ToList();

        if (target.TenantId is null || roles.Contains(Common.Constants.Roles.SuperAdmin, StringComparer.Ordinal))
        {
            throw new ForbiddenException(
                "cannot_suspend_platform_staff",
                "Platform staff accounts can't be suspended from the security screen.");
        }

        if (target.TenantId != tenantId)
        {
            throw new NotFoundException("Employee", PublicId.From(PublicId.Employee, userId));
        }

        if (!byPlatformStaff && roles.Contains(Common.Constants.Roles.Admin, StringComparer.Ordinal))
        {
            throw new ForbiddenException(
                "cannot_suspend_admin",
                "Workspace admins can't be suspended here. Change their role first, or contact support.");
        }

        return target;
    }

    private async Task<EmployeeSecurityResponse> RowAsync(
        long tenantId,
        long userId,
        bool byPlatformStaff,
        long viewerUserId,
        CancellationToken cancellationToken)
    {
        var organization = await GetOrganizationAsync(tenantId, byPlatformStaff, viewerUserId, cancellationToken);
        var publicId = PublicId.From(PublicId.Employee, userId);

        return organization.Employees.FirstOrDefault(employee => employee.UserId == publicId)
               ?? throw new NotFoundException("Employee", publicId);
    }

    /// <summary>Tells the workspace's admins that platform staff suspended one of them.</summary>
    /// <remarks>
    /// Including the suspended admin themself, when it is an admin: they cannot sign in to read it
    /// until reactivated, but the record should exist when they do.
    /// </remarks>
    private async Task NotifyWorkspaceAdminsAsync(long tenantId, User target, CancellationToken cancellationToken)
    {
        var administrators = await _users.GetTenantAdministratorsAsync(tenantId, cancellationToken);
        var now = _clock.UtcNow;

        foreach (var administrator in administrators)
        {
            _notifications.Add(new Notification
            {
                TenantId = tenantId,
                UserId = administrator.Id,
                Kind = NotificationKind.SecurityAccountSuspended,
                Title = $"{target.DisplayName}'s account was suspended",
                Body = $"Platform support suspended {target.DisplayName} ({target.Email}) after a security review. "
                       + "They can't sign in until the account is reactivated.",
                Priority = NotificationPriority.Warning,
                Icon = "shield-exclamation",
                ActionLabel = "Review security",
                ActionRoute = "/settings/security",
                OccurredOn = now,
            });
        }
    }

    private void Audit(long tenantId, long actorUserId, long targetUserId, string eventName, object detail) =>
        _audit.Add(new AuditLog
        {
            TenantId = tenantId,
            UserId = actorUserId,
            EntityName = eventName,
            EntityId = targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Action = Common.Constants.AppConstants.AuditAction.Updated,
            Changes = System.Text.Json.JsonSerializer.Serialize(detail),
            CorrelationId = _requestContext.CorrelationId,
            IpAddress = _requestContext.IpAddress,
            OccurredOn = _clock.UtcNow,
        });

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceResponse>> GetDevicesAsync(
        long? tenantId,
        long userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var windowStart = now.AddDays(-_policy.DeviceWindowDays);
        var activeSince = now.AddMinutes(-_policy.ActiveWindowMinutes);

        if (tenantId is { } expected)
        {
            var belongs = await _queries.CountAsync(
                _users.Query().IgnoreQueryFilters().Where(user => user.Id == userId && user.TenantId == expected && !user.IsDeleted),
                cancellationToken);

            if (belongs == 0)
            {
                throw new NotFoundException("Employee", PublicId.From(PublicId.Employee, userId));
            }
        }

        var sessions = await _queries.ToListAsync(
            _sessions.Query().IgnoreQueryFilters()
                .Where(session => !session.IsDeleted && session.UserId == userId && session.LastActivityAt >= windowStart)
                .OrderByDescending(session => session.LastActivityAt),
            cancellationToken);

        return
        [
            .. sessions
                .GroupBy(session => session.DeviceId, StringComparer.Ordinal)
                .Select(device =>
                {
                    // Newest first, so the head of each group is what the device looks like now.
                    var latest = device.First();

                    return new DeviceResponse(
                        PublicId.From(PublicId.Session, latest.Id),
                        latest.DeviceLabel,
                        latest.Browser,
                        latest.OperatingSystem,
                        latest.DeviceType,
                        latest.LastIpAddress ?? latest.IpAddress,
                        latest.Location,
                        device.Min(session => session.CreatedOn),
                        latest.LastActivityAt,
                        latest.RevokedAt == null && latest.LastActivityAt >= activeSince,
                        currentSessionId is { } current && device.Any(session => session.SessionId == current),
                        latest.RevokedAt == null,
                        device.Count());
                })
                .OrderByDescending(device => device.IsCurrent)
                .ThenByDescending(device => device.LastActiveAt),
        ];
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        string sessionId,
        long? tenantId,
        long? ownerUserId,
        long revokedByUserId,
        CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Session, sessionId, "session");

        var session = await _queries.FirstOrDefaultAsync(
            _sessions.Query(asNoTracking: false).IgnoreQueryFilters()
                .Where(candidate =>
                    candidate.Id == id
                    && !candidate.IsDeleted
                    && (tenantId == null || candidate.TenantId == tenantId)
                    && (ownerUserId == null || candidate.UserId == ownerUserId)),
            cancellationToken)
            ?? throw new NotFoundException("Session", sessionId);

        // A person ending their own session is signing out, not an administrator acting on someone;
        // only the second is recorded as a security event.
        var byAdministrator = session.UserId != revokedByUserId;

        await _tracker.RevokeAsync(
            session,
            byAdministrator ? "revoked_by_admin" : "revoked_by_user",
            byAdministrator ? revokedByUserId : null,
            cancellationToken);
    }
}
