using System.Text.Json;
using Marketing.Common.Enums;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Marketing.DataAccess.Interceptors;

/// <summary>
/// Writes an append-only audit trail row for every entity change, in the same transaction as the
/// change itself.
/// <para>
/// Same transaction is the point: an audit trail written afterwards, or to a different store, can
/// disagree with the data it describes when a request fails midway. Here a change and its audit
/// row commit together or not at all.
/// </para>
/// <para>
/// Must be registered <em>after</em> <see cref="AuditingSaveChangesInterceptor"/> - interceptors
/// run in registration order, and this one records the audit columns that one populates.
/// </para>
/// </summary>
public sealed class AuditTrailInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// Columns whose values must never reach the audit trail. Recording a password hash or a
    /// refresh-token hash would turn the audit table into a second, less-guarded credential store.
    /// </summary>
    private static readonly HashSet<string> RedactedProperties = new(StringComparer.Ordinal)
    {
        nameof(User.PasswordHash),
        nameof(User.SecurityStamp),
        nameof(RefreshToken.TokenHash),
        nameof(RefreshToken.ReplacedByTokenHash),
    };

    /// <summary>Entity types that are not themselves audited.</summary>
    private static readonly HashSet<Type> ExcludedTypes = [typeof(RefreshToken)];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly ICurrentUser _currentUser;
    private readonly IRequestContext _requestContext;
    private readonly IDateTimeProvider _dateTimeProvider;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="currentUser">Principal to attribute the change to.</param>
    /// <param name="requestContext">Correlation metadata for the operation.</param>
    /// <param name="dateTimeProvider">Clock.</param>
    public AuditTrailInterceptor(
        ICurrentUser currentUser,
        IRequestContext requestContext,
        IDateTimeProvider dateTimeProvider)
    {
        _currentUser = currentUser;
        _requestContext = requestContext;
        _dateTimeProvider = dateTimeProvider;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var utcNow = _dateTimeProvider.UtcNow;
        var userId = _currentUser.AuditUserId;

        // Materialise first: adding audit rows mutates the change tracker, and enumerating it
        // while it is being modified would throw.
        var auditable = context.ChangeTracker
            .Entries<BaseEntity>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .Where(entry => !ExcludedTypes.Contains(entry.Entity.GetType()))
            .ToList();

        if (auditable.Count == 0)
        {
            return;
        }

        var logs = new List<AuditLog>(auditable.Count);

        foreach (var entry in auditable)
        {
            var action = ResolveAction(entry);
            var changes = BuildChangeSet(entry, action);

            if (changes.Count == 0)
            {
                continue;
            }

            logs.Add(new AuditLog
            {
                Id = SequentialGuid.Create(utcNow),
                TenantId = entry.Entity.TenantId,
                UserId = userId,
                EntityName = entry.Entity.GetType().Name,
                EntityId = entry.Entity.Id.ToString(),
                Action = action,
                Changes = JsonSerializer.Serialize(changes, SerializerOptions),
                CorrelationId = _requestContext.CorrelationId,
                IpAddress = _requestContext.IpAddress,
                OccurredOn = utcNow,
            });
        }

        if (logs.Count > 0)
        {
            context.Set<AuditLog>().AddRange(logs);
        }
    }

    private static AuditAction ResolveAction(EntityEntry<BaseEntity> entry)
    {
        if (entry.State == EntityState.Added)
        {
            return AuditAction.Created;
        }

        // The auditing interceptor has already rewritten hard deletes into an IsDeleted update, so
        // a delete arrives here as a modification with that flag newly set.
        var deletedFlag = entry.Property(nameof(BaseEntity.IsDeleted));

        return deletedFlag.IsModified && deletedFlag.CurrentValue is true
            ? AuditAction.Deleted
            : AuditAction.Updated;
    }

    private static Dictionary<string, object?> BuildChangeSet(EntityEntry<BaseEntity> entry, AuditAction action)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            if (property.Metadata.IsPrimaryKey() || name == nameof(BaseEntity.RowVersion))
            {
                continue;
            }

            if (action != AuditAction.Created && !property.IsModified)
            {
                continue;
            }

            if (RedactedProperties.Contains(name))
            {
                changes[name] = new { redacted = true };
                continue;
            }

            changes[name] = action == AuditAction.Created
                ? new { @new = property.CurrentValue }
                : new { old = property.OriginalValue, @new = property.CurrentValue };
        }

        return changes;
    }
}
