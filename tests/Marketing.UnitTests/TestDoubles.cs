using Marketing.Common.Constants;
using Marketing.DataAccess.Context;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

// StubCurrentUser exposes a Roles property, which would otherwise shadow the Roles constant class.
using RoleCatalog = Marketing.Common.Constants.Roles;

namespace Marketing.UnitTests;

/// <summary>Deterministic clock.</summary>
public sealed class FixedDateTimeProvider : IDateTimeProvider
{
    public FixedDateTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }

    public DateTime UtcNowDateTime => UtcNow.UtcDateTime;
}

/// <summary>Tenant context with directly settable state.</summary>
public sealed class StubTenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }

    public string? TenantSlug { get; set; }

    public bool HasTenant => TenantId is not null;

    public bool CanAccessAllTenants { get; set; }

    public Guid RequireTenantId() =>
        TenantId ?? throw new Marketing.Common.Exceptions.TenantResolutionException();

    public IDisposable BeginScope(Guid tenantId, string? tenantSlug = null)
    {
        var previous = TenantId;
        TenantId = tenantId;
        TenantSlug = tenantSlug;

        return new Restore(() => TenantId = previous);
    }

    private sealed class Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

/// <summary>Principal with directly settable state.</summary>
public sealed class StubCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }

    public Guid? SessionId { get; set; }

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    public IReadOnlyCollection<string> Roles { get; set; } = [];

    public IReadOnlyCollection<string> Permissions { get; set; } = [];

    public bool IsAuthenticated => UserId is not null;

    public bool IsSuperAdmin => IsInRole(RoleCatalog.SuperAdmin);

    public Guid AuditUserId => UserId ?? AppConstants.Platform.SystemUserId;

    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.Ordinal);

    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>Request context with fixed correlation metadata.</summary>
public sealed class StubRequestContext : IRequestContext
{
    public string CorrelationId { get; set; } = "test-correlation-id";

    public string? IpAddress { get; set; } = "203.0.113.7";

    public string? UserAgent { get; set; } = "xunit";
}

/// <summary>Builds a context whose model is real but which never opens a connection.</summary>
public static class TestDbContextFactory
{
    private const string OfflineConnectionString =
        "Host=localhost;Port=5432;Database=marketing_tests;Username=test;Password=test";

    /// <summary>
    /// Creates a context against the Npgsql provider without connecting.
    /// <para>
    /// The PostgreSQL provider is used rather than the in-memory one on purpose: the entity
    /// configurations declare relational concepts - table names, partial indexes, the xmin
    /// concurrency token - that the in-memory provider rejects outright. Since these tests only
    /// exercise change-tracking behaviour, the model is built and the connection is never opened.
    /// </para>
    /// </summary>
    public static TestApplicationDbContext Create(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<TestApplicationDbContext>()
            .UseNpgsql(OfflineConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new TestApplicationDbContext(options, tenantContext);
    }
}

/// <summary>
/// Adds a probe entity that requires a tenant, so the mandatory-tenant guard can be exercised
/// before the domain modules that would otherwise provide one exist.
/// </summary>
public sealed class TestApplicationDbContext : ApplicationDbContext
{
    public TestApplicationDbContext(DbContextOptions<TestApplicationDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<TenantMandatoryProbe> Probes => Set<TenantMandatoryProbe>();

    protected override void OnAdditionalModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantMandatoryProbe>(entity =>
        {
            entity.ToTable("probes");
            entity.HasKey(probe => probe.Id);
            entity.Property(probe => probe.Id).ValueGeneratedNever();
            entity.Property(probe => probe.Label).HasMaxLength(64);
            entity.Ignore(probe => probe.RowVersion);
        });
    }
}

/// <summary>Stands in for a domain entity where a missing tenant is always a defect.</summary>
public sealed class TenantMandatoryProbe : Marketing.DataAccess.Entities.BaseEntity,
    Marketing.DataAccess.Entities.IRequiresTenant
{
    public string Label { get; set; } = "probe";
}
