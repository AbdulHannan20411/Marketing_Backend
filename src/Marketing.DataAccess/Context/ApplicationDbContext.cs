using System.Reflection;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Marketing.DataAccess.Context;

/// <summary>
/// The EF Core unit of work for the platform.
/// <para>
/// Two invariants are enforced here rather than left to callers: soft-deleted rows are invisible,
/// and tenant-owned rows are invisible outside their tenant. Both are implemented as global query
/// filters so that forgetting a <c>WHERE</c> clause in a new repository cannot leak data.
/// </para>
/// </summary>
public class ApplicationDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Provider options supplied by dependency injection.</param>
    /// <param name="tenantContext">Ambient tenant, consulted by the global query filters.</param>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Initialises a derived context.
    /// <para>
    /// The type is left open and this overload accepts the non-generic options purely so tests can
    /// derive a context that maps an extra probe entity. Invariants such as the mandatory-tenant
    /// guard have no entity to exercise them until the domain modules land, and an untested
    /// isolation invariant is worth less than a slightly more permissive base class.
    /// </para>
    /// </summary>
    /// <param name="options">Provider options.</param>
    /// <param name="tenantContext">Ambient tenant.</param>
    protected ApplicationDbContext(DbContextOptions options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>Customer organisations.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>User accounts.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Platform roles.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>Role assignments.</summary>
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    /// <summary>Refresh-token sessions.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>Append-only audit trail.</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Per-user adjustments to role-granted permissions.</summary>
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();

    /// <summary>Reusable permission bundles.</summary>
    public DbSet<PermissionSet> PermissionSets => Set<PermissionSet>();

    /// <summary>Contacts.</summary>
    public DbSet<Contact> Contacts => Set<Contact>();

    /// <summary>Contact groups.</summary>
    public DbSet<ContactGroup> ContactGroups => Set<ContactGroup>();

    /// <summary>Group memberships.</summary>
    public DbSet<ContactGroupMember> ContactGroupMembers => Set<ContactGroupMember>();

    /// <summary>Tags.</summary>
    public DbSet<ContactTag> ContactTags => Set<ContactTag>();

    /// <summary>Tag assignments.</summary>
    public DbSet<ContactTagAssignment> ContactTagAssignments => Set<ContactTagAssignment>();

    /// <summary>WhatsApp Business Account connections.</summary>
    public DbSet<WhatsAppConnection> WhatsAppConnections => Set<WhatsAppConnection>();

    /// <summary>Message templates.</summary>
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    /// <summary>Campaigns.</summary>
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    /// <summary>Firings of a recurring campaign.</summary>
    public DbSet<CampaignRun> CampaignRuns => Set<CampaignRun>();

    /// <summary>Per-recipient campaign messages.</summary>
    public DbSet<CampaignMessage> CampaignMessages => Set<CampaignMessage>();

    /// <summary>Delivery failures.</summary>
    public DbSet<DeliveryFailure> DeliveryFailures => Set<DeliveryFailure>();

    /// <summary>Pre-aggregated daily messaging counters.</summary>
    public DbSet<MessageDailyStat> MessageDailyStats => Set<MessageDailyStat>();

    /// <summary>Dashboard activity feed.</summary>
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();

    /// <summary>Subscription plans.</summary>
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();

    /// <summary>Tenant subscriptions.</summary>
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();

    /// <summary>Invoices.</summary>
    public DbSet<Invoice> Invoices => Set<Invoice>();

    /// <summary>Payments.</summary>
    public DbSet<Payment> Payments => Set<Payment>();

    /// <summary>Renewal records.</summary>
    public DbSet<RenewalRecord> RenewalRecords => Set<RenewalRecord>();

    /// <summary>Stored payment instruments, held as processor tokens only.</summary>
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();

    /// <summary>Invoice addresses.</summary>
    public DbSet<BillingProfile> BillingProfiles => Set<BillingProfile>();

    /// <summary>Notifications.</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Single-use invitation and password-reset tokens.</summary>
    public DbSet<UserToken> UserTokens => Set<UserToken>();

    /// <summary>Staged CSV import batches.</summary>
    public DbSet<ContactImportBatch> ContactImportBatches => Set<ContactImportBatch>();

    /// <summary>Staged CSV import rows.</summary>
    public DbSet<ContactImportRow> ContactImportRows => Set<ContactImportRow>();

    /// <summary>Failed-record exports asked for against an import.</summary>
    public DbSet<ContactImportExport> ContactImportExports => Set<ContactImportExport>();

    /// <summary>Manual payment submissions awaiting or past review.</summary>
    public DbSet<PaymentRequest> PaymentRequests => Set<PaymentRequest>();

    /// <summary>Where customers can send money, and how they are told to.</summary>
    public DbSet<PaymentChannelSetting> PaymentChannelSettings => Set<PaymentChannelSetting>();

    /// <summary>The import outbox. Queued work, claimed by the import workers.</summary>
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    /// <summary>Transactional email waiting to be delivered.</summary>
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();

    /// <summary>
    /// Tenant applied by the global query filter.
    /// <para>
    /// Public because the filter expression must reference it as a member of <c>this</c>. That is
    /// what makes EF Core lift the value into a query parameter evaluated per query, instead of
    /// baking the model-building context's value into the cached model.
    /// </para>
    /// </summary>
    public long? CurrentTenantId => _tenantContext.TenantId;

    /// <summary>Whether the tenant filter is bypassed. True only for platform administrators.</summary>
    public bool BypassTenantFilter => _tenantContext.CanAccessAllTenants;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        OnAdditionalModelCreating(modelBuilder);

        var applyFilters = typeof(ApplicationDbContext)
            .GetMethod(nameof(ApplyGlobalFilters), BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (!typeof(BaseEntity).IsAssignableFrom(clrType))
            {
                continue;
            }

            applyFilters.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
        }

        base.OnModelCreating(modelBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every instant in the schema is an absolute point in time. Pinning the store type here
        // means no configuration can accidentally introduce a local-time column.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateTimeOffset?>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);

        base.ConfigureConventions(configurationBuilder);
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Translate at the boundary so no upper layer has to reference EF Core to handle a
            // conflict, and so the API returns a 409 rather than a 500.
            var resource = exception.Entries.Count > 0
                ? exception.Entries[0].Entity.GetType().Name
                : "Record";

            throw new ConcurrencyConflictException(resource, exception);
        }
    }

    /// <summary>
    /// Extension point for derived contexts to register additional entities before the global
    /// filters are attached. Empty in production.
    /// </summary>
    /// <param name="modelBuilder">Model builder.</param>
    protected virtual void OnAdditionalModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <summary>
    /// Attaches the soft-delete filter to every entity, and the tenant filter to entities that
    /// declare themselves <see cref="ITenantScoped"/>.
    /// </summary>
    /// <typeparam name="TEntity">Entity being filtered.</typeparam>
    /// <param name="modelBuilder">Model builder.</param>
    private void ApplyGlobalFilters<TEntity>(ModelBuilder modelBuilder)
        where TEntity : BaseEntity
    {
        if (typeof(ITenantScoped).IsAssignableFrom(typeof(TEntity)))
        {
            modelBuilder.Entity<TEntity>().HasQueryFilter(entity =>
                !entity.IsDeleted && (BypassTenantFilter || entity.TenantId == CurrentTenantId));

            return;
        }

        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => !entity.IsDeleted);
    }
}
