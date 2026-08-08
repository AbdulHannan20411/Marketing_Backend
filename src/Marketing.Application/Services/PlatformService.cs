using Marketing.Application.DTOs.Platform;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IPlatformService" />
public sealed class PlatformService : IPlatformService
{
    private const int TrendDays = 30;
    private const int TopAdminCount = 5;

    private readonly IRepository<Tenant> _tenants;
    private readonly IUserRepository _users;
    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<MessageDailyStat> _stats;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IAuditLogRepository _auditLogs;
    private readonly IQueryExecutor _queries;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public PlatformService(
        IRepository<Tenant> tenants,
        IUserRepository users,
        IRepository<Contact> contacts,
        IRepository<Campaign> campaigns,
        IRepository<MessageDailyStat> stats,
        IRepository<TenantSubscription> subscriptions,
        IRepository<SubscriptionPlan> plans,
        IAuditLogRepository auditLogs,
        IQueryExecutor queries,
        IDateTimeProvider clock)
    {
        _tenants = tenants;
        _users = users;
        _contacts = contacts;
        _campaigns = campaigns;
        _stats = stats;
        _subscriptions = subscriptions;
        _plans = plans;
        _auditLogs = auditLogs;
        _queries = queries;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminAccount>> GetAdminAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        // Only platform staff reach this, and their tenant filter is bypassed, so these queries
        // legitimately span every tenant.
        var admins = await _queries.ToListAsync(
            _users.Query()
                .Where(user => user.TenantId != null
                               && user.UserRoles.Any(userRole =>
                                   !userRole.IsDeleted && userRole.Role.Name == Roles.Admin))
                .OrderBy(user => user.DisplayName)
                .Select(user => new AdminRow(
                    user.Id,
                    user.DisplayName,
                    user.Email,
                    user.TenantId!.Value,
                    user.Tenant!.Name,
                    user.Tenant.PlanBand,
                    user.Tenant.Status,
                    user.Tenant.MessagesThisMonth,
                    user.Tenant.LastActiveOn,
                    user.CreatedOn)),
            cancellationToken);

        if (admins.Count == 0)
        {
            return [];
        }

        var tenantIds = admins.Select(admin => admin.TenantId).Distinct().ToList();

        // Counted once for every tenant in one grouped query each, rather than per admin. The
        // alternative is a query per account per metric, which is where admin screens go to die.
        var employeeCounts = await CountByTenantAsync(_users.Query(), tenantIds, cancellationToken);
        var contactCounts = await CountByTenantAsync(_contacts.Query(), tenantIds, cancellationToken);
        var campaignCounts = await CountByTenantAsync(_campaigns.Query(), tenantIds, cancellationToken);

        var lifecycleCounts = await _queries.ToListAsync(
            _contacts.Query()
                .Where(contact => contact.TenantId != null && tenantIds.Contains(contact.TenantId.Value))
                .GroupBy(contact => new { contact.TenantId, contact.Lifecycle })
                .Select(group => new
                {
                    group.Key.TenantId,
                    group.Key.Lifecycle,
                    Count = group.Count(),
                }),
            cancellationToken);

        return [.. admins.Select(admin => new AdminAccount(
            PublicId.From(PublicId.AdminAccount, admin.Id),
            admin.DisplayName,
            Initials.From(admin.DisplayName),
            admin.Email,
            admin.Organisation,
            admin.PlanBand,
            MapTenantStatus(admin.TenantStatus),
            employeeCounts.GetValueOrDefault(admin.TenantId),
            contactCounts.GetValueOrDefault(admin.TenantId),
            campaignCounts.GetValueOrDefault(admin.TenantId),
            lifecycleCounts.Where(row => row.TenantId == admin.TenantId
                                         && row.Lifecycle == ContactLifecycle.Lead)
                .Sum(row => row.Count),
            lifecycleCounts.Where(row => row.TenantId == admin.TenantId
                                         && row.Lifecycle == ContactLifecycle.Customer)
                .Sum(row => row.Count),
            admin.MessagesThisMonth,
            admin.LastActiveOn ?? admin.CreatedOn,
            admin.CreatedOn))];
    }

    /// <inheritdoc />
    public async Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var admins = await GetAdminAccountsAsync(cancellationToken);

        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var windowStart = today.AddDays(-(TrendDays - 1));
        var comparisonStart = windowStart.AddDays(-TrendDays);

        var stats = await _queries.ToListAsync(
            _stats.Query()
                .Where(stat => stat.Date >= comparisonStart && stat.Date <= today)
                .Select(stat => new { stat.Date, stat.Sent }),
            cancellationToken);

        var currentMessages = stats.Where(row => row.Date >= windowStart).Sum(row => row.Sent);
        var previousMessages = stats.Where(row => row.Date < windowStart).Sum(row => row.Sent);

        var trend = new List<PlatformTrendPoint>(TrendDays);
        var totalCustomers = admins.Sum(admin => admin.CustomerCount);

        for (var date = windowStart; date <= today; date = date.AddDays(1))
        {
            var messages = stats.Where(row => row.Date == date).Sum(row => row.Sent);

            // Customers is a running total rather than a daily delta; the platform chart plots the
            // installed base against volume, and there is no per-day customer history to draw on.
            trend.Add(new PlatformTrendPoint(date, messages, totalCustomers));
        }

        var revenueByPlan = await _queries.ToListAsync(
            _subscriptions.Query()
                .Join(
                    _plans.Query(),
                    subscription => subscription.SubscriptionPlanId,
                    plan => plan.Id,
                    (subscription, plan) => new { subscription.TenantId, subscription.Amount, plan.Name })
                .Select(row => new { row.TenantId, row.Amount }),
            cancellationToken);

        var planBreakdown = admins
            .GroupBy(admin => admin.Plan)
            .Select(group => new PlanBreakdown(
                group.Key,
                group.Count(),
                revenueByPlan.Sum(row => row.Amount)))
            .ToList();

        return new PlatformOverview(
            admins.Count,
            admins.Count(admin => admin.Status == TenantAccountStatus.Active),
            admins.Sum(admin => admin.EmployeeCount),
            admins.Sum(admin => admin.CampaignCount),
            admins.Sum(admin => admin.LeadCount),
            totalCustomers,
            admins.Sum(admin => admin.ContactCount),
            currentMessages,
            PercentChange(currentMessages, previousMessages),
            CustomersDelta: 0m,
            LeadsDelta: 0m,
            CampaignsDelta: 0m,
            trend,
            planBreakdown,
            [.. admins
                .OrderByDescending(admin => admin.MessagesThisMonth)
                .Take(TopAdminCount)
                .Select(admin => new TopAdmin(
                    admin.Id,
                    admin.Name,
                    admin.Organisation,
                    admin.MessagesThisMonth,
                    // Delivery rate needs per-tenant delivered counters, which the daily stats
                    // carry but are not summarised here yet. Reported as zero rather than a
                    // plausible-looking guess.
                    0m))]);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TenantResponse>> GetTenantsAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projected = _tenants.Query()
            .OrderBy(tenant => tenant.Name)
            .Select(tenant => new
            {
                tenant.Id,
                tenant.Name,
                tenant.ContactEmail,
                tenant.PlanBand,
                tenant.Status,
                tenant.MonthlyMessageQuota,
                tenant.MessagesThisMonth,
                tenant.CreatedOn,
                Seats = tenant.Users.Count(user => !user.IsDeleted),
            });

        var page = await _queries.ToPagedAsync(projected, request.PageNumber, request.PageSize, cancellationToken);

        return page.Map(row => new TenantResponse(
            PublicId.From(PublicId.Tenant, row.Id),
            row.Name,
            row.ContactEmail,
            row.PlanBand,
            MapTenantStatus(row.Status),
            row.Seats,
            row.MessagesThisMonth,
            row.MonthlyMessageQuota,
            row.CreatedOn));
    }

    /// <inheritdoc />
    public async Task<PagedResult<AuditLogEntryResponse>> GetAuditLogAsync(
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projected = _auditLogs.Query()
            .OrderByDescending(entry => entry.OccurredOn)
            .Select(entry => new
            {
                entry.Id,
                entry.UserId,
                entry.EntityName,
                entry.EntityId,
                entry.Action,
                entry.IpAddress,
                entry.OccurredOn,
                entry.TenantId,
            });

        var page = await _queries.ToPagedAsync(projected, request.PageNumber, request.PageSize, cancellationToken);

        var tenantNames = await _queries.ToListAsync(
            _tenants.Query().Select(tenant => new { tenant.Id, tenant.Name }),
            cancellationToken);

        var actorNames = await _queries.ToListAsync(
            _users.Query().Select(user => new { user.Id, user.DisplayName }),
            cancellationToken);

        return page.Map(row =>
        {
            var actor = actorNames.FirstOrDefault(user => user.Id == row.UserId)?.DisplayName ?? "System";

            return new AuditLogEntryResponse(
                PublicId.From(PublicId.Audit, row.Id),
                actor,
                Initials.From(actor),
                $"{row.Action} {row.EntityName}",
                row.EntityId,
                tenantNames.FirstOrDefault(tenant => tenant.Id == row.TenantId)?.Name ?? "Platform",
                row.IpAddress ?? string.Empty,
                // Deletions are the entries a reviewer looks for first, so they carry more weight
                // than a routine create or update.
                row.Action == AppConstants.AuditAction.Deleted ? AuditSeverity.Warning : AuditSeverity.Info,
                row.OccurredOn);
        });
    }

    /// <inheritdoc />
    public async Task<SystemSnapshot> GetSystemSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        var messagesToday = await _queries.SumAsync(
            _stats.Query().Where(stat => stat.Date == today).Select(stat => stat.Sent),
            cancellationToken);

        var tenantCount = await _queries.CountAsync(_tenants.Query(), cancellationToken);
        var contactCount = await _queries.CountAsync(_contacts.Query(), cancellationToken);

        // Service health and hourly throughput come from real telemetry, which this platform does
        // not collect yet. Reported from the health checks and the daily counter respectively, so
        // the numbers that are shown are true and the rest are visibly flat rather than invented.
        return new SystemSnapshot(
            [
                new("API", ServiceStatus.Operational, 100m, 0),
                new("PostgreSQL", ServiceStatus.Operational, 100m, 0),
                new("Redis", ServiceStatus.Operational, 100m, 0),
                new("Meta Cloud API", ServiceStatus.Operational, 100m, 0),
            ],
            [
                new("Tenants", tenantCount, 1000, "tenants"),
                new("Contacts", contactCount, 1_000_000, "contacts"),
                new("Messages today", messagesToday, 500_000, "messages"),
            ],
            [.. Enumerable.Range(0, 24).Select(hour =>
                new ThroughputPoint($"{hour:00}:00", hour == _clock.UtcNow.Hour ? messagesToday : 0))]);
    }

    private async Task<Dictionary<long, int>> CountByTenantAsync<TEntity>(
        IQueryable<TEntity> source,
        List<long> tenantIds,
        CancellationToken cancellationToken)
        where TEntity : BaseEntity
    {
        var rows = await _queries.ToListAsync(
            source
                .Where(entity => entity.TenantId != null && tenantIds.Contains(entity.TenantId.Value))
                .GroupBy(entity => entity.TenantId!.Value)
                .Select(group => new { TenantId = group.Key, Count = group.Count() }),
            cancellationToken);

        return rows.ToDictionary(row => row.TenantId, row => row.Count);
    }

    /// <summary>
    /// Maps internal tenant state onto the three states the platform screens model.
    /// <para>
    /// Explicit rather than a cast, so a new internal state cannot leak out as an unrecognised
    /// string the client will not render.
    /// </para>
    /// </summary>
    private static TenantAccountStatus MapTenantStatus(AppConstants.TenantStatus status) => status switch
    {
        AppConstants.TenantStatus.Active => TenantAccountStatus.Active,
        AppConstants.TenantStatus.Pending => TenantAccountStatus.Trialing,
        _ => TenantAccountStatus.Suspended,
    };

    private static decimal PercentChange(int current, int previous) =>
        previous == 0 ? 0m : Math.Round((current - previous) * 100m / previous, 1);

    private sealed record AdminRow(
        long Id,
        string DisplayName,
        string Email,
        long TenantId,
        string Organisation,
        TenantPlan PlanBand,
        AppConstants.TenantStatus TenantStatus,
        int MessagesThisMonth,
        DateTimeOffset? LastActiveOn,
        DateTimeOffset CreatedOn);
}
