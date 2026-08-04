using Marketing.DataAccess.Context;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Marketing.DataAccess.Factories;

/// <summary>
/// Builds a context for <c>dotnet ef migrations</c>, which runs outside the host and therefore has
/// no dependency injection container, configuration or HTTP request.
/// </summary>
/// <remarks>
/// The connection string is read from <c>MARKETING_MIGRATIONS_CONNECTION</c> and falls back to the
/// local docker-compose instance. Design time never connects for <c>migrations add</c>; the string
/// only has to be syntactically valid.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string EnvironmentVariableName = "MARKETING_MIGRATIONS_CONNECTION";

    private const string LocalDefault =
        "Host=localhost;Port=5432;Database=marketing;Username=marketing;Password=marketing_dev_password";

    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariableName) ?? LocalDefault;

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ApplicationDbContext(options, new DesignTimeTenantContext());
    }

    /// <summary>
    /// Tenant context used while scaffolding. Reports no tenant and no bypass, so the generated
    /// migration reflects the same filtered model the application runs against.
    /// </summary>
    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;

        public string? TenantSlug => null;

        public bool HasTenant => false;

        public bool CanAccessAllTenants => false;

        public Guid RequireTenantId() =>
            throw new InvalidOperationException("No tenant is available at design time.");

        public IDisposable BeginScope(Guid tenantId, string? tenantSlug = null) => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
                // Nothing to restore at design time.
            }
        }
    }
}
