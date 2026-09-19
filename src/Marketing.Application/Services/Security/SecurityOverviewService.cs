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
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<OrganizationSecurityResponse> GetOrganizationAsync(
        long tenantId,
        bool includeRisk,
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
        IOptions<AuthenticationPolicyOptions> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

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
                }),
            cancellationToken);

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
                risk));
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
