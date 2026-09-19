using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>A workspace's auto-reply knowledge entries.</summary>
public interface IAutoReplyKnowledgeRepository : IRepository<AutoReplyKnowledgeEntry>
{
    /// <summary>
    /// Removes every entry the workspace has, for good.
    /// </summary>
    /// <remarks>
    /// Hard delete, run in the database. The file is replaced wholesale on each upload; soft-deleting
    /// would leave five hundred dead rows behind every save, and the rows carry nothing an audit needs.
    /// Runs immediately, not on save, so the caller wraps it in a transaction with the inserts.
    /// </remarks>
    /// <param name="tenantId">Workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were removed.</returns>
    public Task<int> DeleteAllForTenantAsync(long tenantId, CancellationToken cancellationToken = default);
}
