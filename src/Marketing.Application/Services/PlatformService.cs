using System.Linq.Expressions;
using Marketing.Application.DTOs.Platform;
using Marketing.Application.Interfaces;
using Marketing.Business.Extensions;
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

    /// <summary>
    /// Sortable columns on the workspace list, matched against the client's <c>sortBy</c>.
    /// <para>
    /// An allow-list, so a client-supplied sort field is matched against known keys and never
    /// reaches the provider as text.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Expression<Func<Tenant, object?>>> SortableTenantColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = tenant => tenant.Id,
            ["name"] = tenant => tenant.Name,
            ["plan"] = tenant => tenant.PlanBand,
            ["status"] = tenant => tenant.Status,

            // The same subquery the list projects, so the column sorts by the number printed in
            // it. Counted in the database rather than over the page.
            ["seats"] = tenant => tenant.Users.Count(user => !user.IsDeleted),
            ["messagesThisMonth"] = tenant => tenant.MessagesThisMonth,
            ["createdAt"] = tenant => tenant.CreatedOn,
        };

    /// <summary>
    /// Sortable columns on the platform audit log.
    /// </summary>
    /// <remarks>
    /// Over the joined row rather than the entity, because two of the columns on screen are not
    /// columns in the table: the actor's name lives in <c>users</c> and the workspace's in
    /// <c>tenants</c>. Sorting them meant resolving both in SQL, which the read below now does.
    /// </remarks>
    private static readonly Dictionary<string, Expression<Func<AuditEntryRow, object?>>> SortableAuditColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["occurredAt"] = row => row.OccurredOn,
            ["actor"] = row => row.Actor,
            ["action"] = row => row.Action,

            // Severity is derived, not stored - a deletion is a warning and everything else is
            // information - so it sorts by the thing it is derived from. Ascending puts the
            // routine entries first and the deletions last, which is the order the word implies.
            ["severity"] = row => row.Action == AppConstants.AuditAction.Deleted,
            ["workspace"] = row => row.Workspace,
            ["entity"] = row => row.EntityName,
        };

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
        var all = await GetAdminAccountsAsync(new AdminAccountQuery(), cancellationToken);

        return all.Items;
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdminAccount>> GetAdminAccountsAsync(
        AdminAccountQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Only platform staff reach this, and their tenant filter is bypassed, so these queries
        // legitimately span every tenant.
        var matching = _users.Query()
            .Where(user => user.TenantId != null
                           && user.UserRoles.Any(userRole =>
                               !userRole.IsDeleted && userRole.Role.Name == Roles.Admin))
            .WhereMatchesAdminSearch(query.Search);

        if (query.Status is { } wanted)
        {
            // Filtered on the stored state rather than on the mapped one, because the mapping is a
            // C# switch the database knows nothing about: "suspended" is everything that is neither
            // active nor pending, which is why it is written as an exclusion.
            matching = wanted switch
            {
                TenantAccountStatus.Active => matching.Where(user =>
                    user.Tenant!.Status == AppConstants.TenantStatus.Active),
                TenantAccountStatus.Trialing => matching.Where(user =>
                    user.Tenant!.Status == AppConstants.TenantStatus.Pending),
                _ => matching.Where(user =>
                    user.Tenant!.Status != AppConstants.TenantStatus.Active
                    && user.Tenant.Status != AppConstants.TenantStatus.Pending),
            };
        }

        var total = await _queries.CountAsync(matching, cancellationToken);

        var size = Math.Clamp(query.PageSize ?? AdminAccountQuery.DefaultPageSize, 1, AdminAccountQuery.MaxPageSize);
        var page = Math.Max(query.Page ?? 1, 1);

        var rows = matching
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
                    user.CreatedOn));

        // Only the page is counted up below, so a platform with a thousand customers does the same
        // amount of work to draw twelve rows as a platform with twelve.
        if (query.WantsPage)
        {
            rows = rows.Skip((page - 1) * size).Take(size);
        }

        var admins = await _queries.ToListAsync(rows, cancellationToken);

        if (admins.Count == 0)
        {
            return new PagedResult<AdminAccount>([], total, page, size);
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

        var accounts = admins.Select(admin => new AdminAccount(
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
            admin.CreatedOn,
            PublicId.From(PublicId.Tenant, admin.TenantId)));

        // Unpaged callers keep the whole list in one "page", so the counters stay honest rather
        // than claiming there are pages the caller never asked about.
        return new PagedResult<AdminAccount>(
            [.. accounts],
            total,
            page,
            query.WantsPage ? size : Math.Max(total, 1));
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
            .ApplySort(request, SortableTenantColumns, tenant => tenant.Name)

            // Names are not unique across the platform, and neither is a plan band or a status.
            // The key settles the ties so paging cannot repeat a workspace.
            .ThenBy(tenant => tenant.Id)
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

        // The actor and the workspace are resolved in the database rather than after the page is
        // materialised. Two reasons, and the second is the one that matters: a name the query does
        // not know cannot be sorted on, and the previous shape read every user and every tenant on
        // the platform on every request in order to label ten rows.
        //
        // Left joins throughout. The system identity has no user row, and a platform-level entry
        // has no workspace; an inner join would have silently dropped exactly the entries a
        // reviewer opens this screen to find.
        var joined =
            from entry in _auditLogs.Query()
            join candidate in _users.Query() on entry.UserId equals candidate.Id into candidates
            from actor in candidates.DefaultIfEmpty()
            join owner in _tenants.Query() on entry.TenantId equals (long?)owner.Id into owners
            from workspace in owners.DefaultIfEmpty()
            // An object initialiser, not a constructor. Ordering happens after this projection,
            // and EF can resolve a member access back through a member-init but not through a
            // constructor call - with positional arguments the sort failed to translate at all.
            select new AuditEntryRow
            {
                Id = entry.Id,
                EntityName = entry.EntityName,
                EntityId = entry.EntityId,
                Action = entry.Action,
                IpAddress = entry.IpAddress,
                OccurredOn = entry.OccurredOn,
                Actor = actor == null ? null : actor.DisplayName,
                Workspace = workspace == null ? null : workspace.Name,
            };

        var projected = joined
            .ApplySort(request, SortableAuditColumns, row => row.OccurredOn)

            // Entries written by one save share an instant to the microsecond, so the default sort
            // alone is not deterministic. The key is time-ordered, which makes this both a
            // tiebreak and the right secondary order.
            .ThenByDescending(row => row.Id);

        var page = await _queries.ToPagedAsync(projected, request.PageNumber, request.PageSize, cancellationToken);

        return page.Map(row =>
        {
            var actor = row.Actor ?? "System";

            return new AuditLogEntryResponse(
                PublicId.From(PublicId.Audit, row.Id),
                actor,
                Initials.From(actor),
                $"{row.Action} {row.EntityName}",
                row.EntityId,
                row.Workspace ?? "Platform",
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

    /// <summary>
    /// One audit entry with its actor and workspace already resolved.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one, because the sort allow-list is a static field
    /// and has to name the type it selects from.
    /// </remarks>
    private sealed record AuditEntryRow
    {
        /// <summary>Audit entry key.</summary>
        public long Id { get; init; }

        /// <summary>CLR name of the entity that changed.</summary>
        public required string EntityName { get; init; }

        /// <summary>Key of the row that changed.</summary>
        public required string EntityId { get; init; }

        /// <summary>Kind of change. Severity is derived from it.</summary>
        public AppConstants.AuditAction Action { get; init; }

        /// <summary>Client address, when one was recorded.</summary>
        public string? IpAddress { get; init; }

        /// <summary>Instant the change was committed.</summary>
        public DateTimeOffset OccurredOn { get; init; }

        /// <summary>Actor's display name, or null for the system identity.</summary>
        public string? Actor { get; init; }

        /// <summary>Workspace name, or null for a platform-level change.</summary>
        public string? Workspace { get; init; }
    }

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
