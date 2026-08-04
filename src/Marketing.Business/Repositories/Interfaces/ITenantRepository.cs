using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>Tenant queries that go beyond the generic repository.</summary>
public interface ITenantRepository : IRepository<Tenant>
{
    /// <summary>Loads a tenant by slug.</summary>
    /// <param name="slug">URL-safe tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Returns whether a slug is already in use by a live tenant.</summary>
    /// <param name="slug">Candidate slug.</param>
    /// <param name="excludingTenantId">Tenant to exclude, when checking during a rename.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsSlugTakenAsync(
        string slug,
        Guid? excludingTenantId = null,
        CancellationToken cancellationToken = default);
}
