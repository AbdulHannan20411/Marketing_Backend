using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="IAutoReplyKnowledgeRepository" />
public sealed class AutoReplyKnowledgeRepository : Repository<AutoReplyKnowledgeEntry>, IAutoReplyKnowledgeRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public AutoReplyKnowledgeRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public Task<int> DeleteAllForTenantAsync(long tenantId, CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(entry => entry.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
}
