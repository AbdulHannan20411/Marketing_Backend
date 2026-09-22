using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Marketing.Common.Constants.AppConstants;
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

        // Single-use invitation and reset tokens. Hashed, but a hash in a second table is still a
        // second place to attack.
        nameof(UserToken.TokenHash),

        // The tenant's Meta credential and the PIN its number was registered with. The token is
        // encrypted at rest, which the audit copy would not be reasoning about; the PIN is plain
        // two-factor material for a phone number the customer owns.
        nameof(WhatsAppConnection.EncryptedAccessToken),
        nameof(WhatsAppConnection.RegistrationPin),

        // Email bodies, redacted by name so this covers queued messages as well as templates. An edit
        // is recorded - who, when, and what the subject became - but a body is kilobytes of HTML per
        // row, and a queued message's body carries live, single-use links.
        nameof(EmailTemplate.HtmlBody),
        nameof(EmailTemplate.TextBody),
    };

    /// <summary>Entity types that are not themselves audited.</summary>
    /// <remarks>
    /// Knowledge entries are replaced in bulk and recorded as one explicit entry per upload, without
    /// their content. Reply attempts are operational bookkeeping that repeats the customer's words.
    /// </remarks>
    private static readonly HashSet<Type> ExcludedTypes =
        [typeof(RefreshToken), typeof(AutoReplyKnowledgeEntry), typeof(AutoReplyAttempt)];

    /// <summary>
    /// Columns that describe the change rather than the record.
    /// </summary>
    /// <remarks>
    /// Who and when are already the audit row's own <c>UserId</c> and <c>OccurredOn</c>, and the
    /// history panel puts them in the entry's heading. Recorded as fields as well, they turned
    /// "renamed the tag" into "3 fields changed", two of which repeated the heading.
    /// <para>
    /// <c>IsDeleted</c> belongs here for a different reason: its meaning is the entry's action, and
    /// <see cref="ResolveAction"/> has already read it by the time the change set is built. The
    /// tenant cannot legitimately change at all - the auditing interceptor refuses it - and the
    /// audit row carries its own copy regardless.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> BookkeepingProperties = new(StringComparer.Ordinal)
    {
        nameof(BaseEntity.CreatedBy),
        nameof(BaseEntity.CreatedOn),
        nameof(BaseEntity.ModifiedBy),
        nameof(BaseEntity.ModifiedOn),
        nameof(BaseEntity.DeletedBy),
        nameof(BaseEntity.DeletedOn),
        nameof(BaseEntity.IsDeleted),
        nameof(BaseEntity.RowVersion),
        nameof(BaseEntity.TenantId),
    };

    /// <summary>
    /// Serialisation for the change set.
    /// </summary>
    /// <remarks>
    /// Carries the API's own enum converter, so a colour is recorded as <c>"danger"</c> rather than
    /// as <c>4</c>. The audit trail is the only place these values were ever integers, and a reader
    /// cannot act on a number whose meaning lives in a C# enum. Types that pin their own converter -
    /// the payment enums, the dotted notification kinds - keep their wire spelling here too, which
    /// is the point of reusing the converter rather than calling <c>ToString</c>.
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
            // Deleted as well as Added and Modified. Almost every delete in this platform is
            // rewritten into an IsDeleted update before it reaches here, but not all of them -
            // a join row cleared out in bulk, say - and a delete that leaves no trace is the one
            // entry an investigation always wants.
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
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

            // An update that moved nothing is not history - "somebody pressed Save" would bury the
            // changes that matter. A create or a delete is history whatever its change set holds:
            // a soft delete now sets only bookkeeping columns, which are not recorded as fields,
            // and dropping the row for that reason would lose the delete itself.
            if (changes.Count == 0 && action == AuditAction.Updated)
            {
                continue;
            }

            logs.Add(new AuditLog
            {
                TenantId = entry.Entity.TenantId,
                UserId = userId,
                EntityName = entry.Entity.GetType().Name,
                // Invariant, not the ambient culture. An audit trail is matched and searched by this
                // string, and a culture using non-ASCII digits would write an id that no longer
                // equals the one written under any other culture.
                EntityId = entry.Entity.Id.ToString(CultureInfo.InvariantCulture),
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

        if (entry.State == EntityState.Deleted)
        {
            return AuditAction.Deleted;
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
        var wholeRow = entry.State is EntityState.Added or EntityState.Deleted;

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            if (property.Metadata.IsPrimaryKey() || BookkeepingProperties.Contains(name))
            {
                continue;
            }

            // A create reports every column it set and a hard delete every column it took away;
            // anything arriving as a modification - an update, and a soft delete, which is one -
            // reports only what moved. Keyed on the entry's state rather than on the action,
            // because a soft delete is a Deleted action on a Modified row.
            if (!wholeRow && !property.IsModified)
            {
                continue;
            }

            if (RedactedProperties.Contains(name))
            {
                changes[name] = new { redacted = true };
                continue;
            }

            changes[name] = action switch
            {
                AuditAction.Created => new { @new = property.CurrentValue },

                // The row is going; there is no new value to report, and the old one is the whole
                // point of the entry.
                AuditAction.Deleted when entry.State == EntityState.Deleted =>
                    new { old = property.OriginalValue },

                _ => new { old = property.OriginalValue, @new = property.CurrentValue },
            };
        }

        return changes;
    }
}
