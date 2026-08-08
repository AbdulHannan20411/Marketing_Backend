using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="IWhatsAppConnectionRepository" />
public sealed class WhatsAppConnectionRepository : Repository<WhatsAppConnection>, IWhatsAppConnectionRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public WhatsAppConnectionRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public Task<WhatsAppConnection?> FindByPhoneNumberIdAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default) =>
        Set
            // The webhook has no bearer token and therefore no tenant, so the filter would exclude
            // every candidate. The soft-delete predicate is reapplied by hand, and the caller has
            // already verified the payload signature.
            .IgnoreQueryFilters()
            .Where(connection => !connection.IsDeleted && connection.PhoneNumberId == phoneNumberId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<WhatsAppConnection?> FindForTenantAsync(
        long tenantId,
        CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(connection => !connection.IsDeleted && connection.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);
}
