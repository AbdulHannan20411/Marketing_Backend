using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Marketing.DataAccess.Interceptors;

/// <summary>
/// Populates audit columns, stamps the owning tenant and converts hard deletes into soft deletes.
/// <para>
/// This exists so that no service, repository or controller ever assigns <c>CreatedOn</c>,
/// <c>ModifiedBy</c>, <c>TenantId</c> or <c>IsDeleted</c>. Anything that writes through this
/// <c>DbContext</c> - including a background job or a future bulk import - is audited identically,
/// and there is no code path where someone can forget.
/// </para>
/// </summary>
public sealed class AuditingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _dateTimeProvider;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="currentUser">Principal to attribute changes to.</param>
    /// <param name="tenantContext">Tenant to stamp on new rows.</param>
    /// <param name="dateTimeProvider">Clock, injected so tests are deterministic.</param>
    public AuditingSaveChangesInterceptor(
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IDateTimeProvider dateTimeProvider)
    {
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _dateTimeProvider = dateTimeProvider;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var utcNow = _dateTimeProvider.UtcNow;
        var userId = _currentUser.AuditUserId;

        foreach (var entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampCreation(entry, userId, utcNow);
                    break;

                case EntityState.Modified:
                    StampModification(entry, userId, utcNow);
                    break;

                case EntityState.Deleted:
                    ConvertToSoftDelete(entry, userId, utcNow);
                    break;

                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    private void StampCreation(EntityEntry<BaseEntity> entry, long userId, DateTimeOffset utcNow)
    {
        entry.Entity.CreatedBy = userId;
        entry.Entity.CreatedOn = utcNow;
        entry.Entity.IsDeleted = false;

        if (entry.Entity is not ITenantScoped)
        {
            return;
        }

        // A caller may legitimately set the tenant explicitly - a platform administrator creating a
        // user inside a tenant, or a webhook resolving the tenant from a phone number. Otherwise
        // the ambient tenant wins.
        entry.Entity.TenantId ??= _tenantContext.TenantId;

        // For entities where a tenant is mandatory, refuse to write an orphan. Such a row would be
        // excluded by the query filter from the moment it was created, so the failure would only
        // surface much later as "the record I just saved does not exist".
        if (entry.Entity is IRequiresTenant && entry.Entity.TenantId is null)
        {
            throw new TenantResolutionException(
                $"{entry.Entity.GetType().Name} requires a tenant, but none could be resolved for this operation.");
        }
    }

    private static void StampModification(EntityEntry<BaseEntity> entry, long userId, DateTimeOffset utcNow)
    {
        // Reassigning a row to a different tenant is never a legitimate update. Blocking it here
        // means a mass-assignment bug in a DTO mapping cannot move data across the isolation
        // boundary, no matter which layer introduced it.
        //
        // Guarded the same way the insert path is: TenantId lives on BaseEntity in C#, but entities
        // that own no tenant do not map it — Tenant itself being the obvious one, since a tenant has
        // no owning tenant. Reading the property unconditionally throws on those, which turns any
        // update to an organisation into a 500.
        if (entry.Entity is ITenantScoped)
        {
            var tenantProperty = entry.Property(nameof(BaseEntity.TenantId));

            if (tenantProperty.IsModified &&
                !Equals(tenantProperty.OriginalValue, tenantProperty.CurrentValue))
            {
                throw new ForbiddenException(
                    $"The owning tenant of {entry.Entity.GetType().Name} cannot be changed.");
            }
        }

        entry.Entity.ModifiedBy = userId;
        entry.Entity.ModifiedOn = utcNow;

        // Creation stamps are immutable once written.
        entry.Property(nameof(BaseEntity.CreatedBy)).IsModified = false;
        entry.Property(nameof(BaseEntity.CreatedOn)).IsModified = false;
    }

    private static void ConvertToSoftDelete(EntityEntry<BaseEntity> entry, long userId, DateTimeOffset utcNow)
    {
        entry.State = EntityState.Modified;

        entry.Entity.IsDeleted = true;
        entry.Entity.DeletedBy = userId;
        entry.Entity.DeletedOn = utcNow;
        entry.Entity.ModifiedBy = userId;
        entry.Entity.ModifiedOn = utcNow;

        // Flipping the state to Modified marks every scalar dirty, which would issue an UPDATE of
        // the whole row and could resurrect stale in-memory values. Restrict the write to the
        // delete and modification columns.
        foreach (var property in entry.Properties)
        {
            property.IsModified = property.Metadata.Name is
                nameof(BaseEntity.IsDeleted) or
                nameof(BaseEntity.DeletedBy) or
                nameof(BaseEntity.DeletedOn) or
                nameof(BaseEntity.ModifiedBy) or
                nameof(BaseEntity.ModifiedOn);
        }
    }
}
