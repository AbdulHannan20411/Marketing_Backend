using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Entity Framework implementation of <see cref="ITenantRepository"/>.</summary>
public sealed class TenantRepository : Repository<Tenant>, ITenantRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public TenantRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        Set.AsNoTracking().FirstOrDefaultAsync(tenant => tenant.Slug == slug, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsSlugTakenAsync(
        string slug,
        long? excludingTenantId = null,
        CancellationToken cancellationToken = default) =>
        Set.AsNoTracking()
            .AnyAsync(
                tenant => tenant.Slug == slug && (excludingTenantId == null || tenant.Id != excludingTenantId),
                cancellationToken);
}
