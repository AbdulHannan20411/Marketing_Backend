using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="IAuditLogRepository" />
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    /// <param name="tenantContext">Ambient tenant.</param>
    public AuditLogRepository(ApplicationDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public IQueryable<AuditLog> Query()
    {
        var query = _context.AuditLogs.AsNoTracking();

        // AuditLog carries no global query filter, because it is not a BaseEntity. Tenant scoping
        // is therefore applied here by hand - and it has to be, or a tenant operator reading their
        // own audit screen would see every other tenant's changes.
        if (_tenantContext.CanAccessAllTenants)
        {
            return query;
        }

        var tenantId = _tenantContext.TenantId;

        return query.Where(entry => entry.TenantId == tenantId);
    }
}
