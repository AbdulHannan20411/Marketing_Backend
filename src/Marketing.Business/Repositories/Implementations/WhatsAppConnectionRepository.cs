using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.ContractEnums;

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
            .Where(connection => !connection.IsDeleted
                                 && connection.PhoneNumberId == phoneNumberId
                                 // Disconnecting leaves the number on the row as history, so a
                                 // stale row would otherwise still answer for a number a different
                                 // tenant has since connected - and this query spans every tenant.
                                 // Only a live connection may claim inbound traffic.
                                 && connection.Status != ConnectionStatus.Disconnected)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The workspace's default number - which is what every caller written before there were several
    /// meant by "the connection". Ordered so that a workspace whose default flag is somehow missing
    /// still gets a sensible answer: a connected number before a disconnected one, oldest first.
    /// </remarks>
    public Task<WhatsAppConnection?> FindForTenantAsync(
        long tenantId,
        CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(connection => !connection.IsDeleted && connection.TenantId == tenantId)
            .OrderByDescending(connection => connection.IsDefault)
            .ThenBy(connection => connection.Status == ConnectionStatus.Disconnected)
            .ThenBy(connection => connection.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WhatsAppConnection>> FindAllForTenantAsync(
        long tenantId,
        CancellationToken cancellationToken = default) =>
        await Set
            .IgnoreQueryFilters()
            .Where(connection => !connection.IsDeleted && connection.TenantId == tenantId)
            .OrderByDescending(connection => connection.IsDefault)
            .ThenBy(connection => connection.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<WhatsAppConnection?> FindByIdForTenantAsync(
        long tenantId,
        long connectionId,
        CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(connection => !connection.IsDeleted
                                 && connection.TenantId == tenantId
                                 && connection.Id == connectionId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsWabaInUseElsewhereAsync(
        string wabaId,
        long excludingId,
        CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .AnyAsync(
                connection => !connection.IsDeleted
                              && connection.Id != excludingId
                              && connection.WabaId == wabaId
                              && connection.Status != ConnectionStatus.Disconnected,
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> LabelsForTenantAsync(
        long tenantId,
        CancellationToken cancellationToken = default) =>
        await Set
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(connection => connection.TenantId == tenantId)
            .ToDictionaryAsync(connection => connection.Id, connection => connection.Label, cancellationToken);

    /// <inheritdoc />
    public Task<WhatsAppConnection?> FindForRecordAsync(
        long tenantId,
        long? connectionId,
        CancellationToken cancellationToken = default) =>
        connectionId is { } id
            ? FindByIdForTenantAsync(tenantId, id, cancellationToken)
            : FindForTenantAsync(tenantId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> FindTenantsAwaitingOnboardingAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        await Set
            .IgnoreQueryFilters()
            // The null check is not decoration: TenantId is nullable on the base entity, and a
            // connection without one cannot be scoped to a tenant to be worked on.
            .Where(connection => !connection.IsDeleted
                                 && connection.Status == ConnectionStatus.Pending
                                 && connection.TenantId != null)
            // Oldest first, so a connection that has been waiting is not starved by newer ones.
            .OrderBy(connection => connection.ModifiedOn ?? connection.CreatedOn)
            .Select(connection => connection.TenantId!.Value)
            .Take(limit)
            .ToListAsync(cancellationToken);
}
