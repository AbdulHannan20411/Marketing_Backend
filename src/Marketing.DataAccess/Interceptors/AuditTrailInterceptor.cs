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
    [
        typeof(RefreshToken),
        typeof(AutoReplyKnowledgeEntry),
        typeof(AutoReplyAttempt),

        // A notification is a message about a business record, not a business record. Every one
        // written produced an audit entry saying a message had been written, and clearing a full
        // list would have produced a hundred more that nobody will ever read. The event it refers
        // to is audited where it happens.
        typeof(Notification),
        typeof(NotificationDismissal),
    ];

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

    /// <summary>
    /// Written into <see cref="AuditLog.EntityId"/> for a row whose key does not exist yet.
    /// </summary>
    /// <remarks>
    /// Deliberately not a number. A create used to be recorded against id <c>0</c>, which is a
    /// well-formed id that simply matches no record - so 977 of them sat in the table looking
    /// like data. This cannot be mistaken for a key, and a row still carrying it says exactly
    /// what happened: the change committed, and the second statement that links it did not.
    /// </remarks>
    private const string PendingEntityId = "pending";

    private readonly ICurrentUser _currentUser;
    private readonly IRequestContext _requestContext;
    private readonly IDateTimeProvider _dateTimeProvider;

    /// <summary>
    /// Audit rows for inserts, waiting for the database to say what their key turned out to be.
    /// </summary>
    /// <remarks>
    /// Per request: this interceptor is registered scoped, so the list belongs to one unit of work
    /// and cannot be seen by another. Cleared after every save, successful or not.
    /// </remarks>
    private readonly List<(AuditLog Log, BaseEntity Entity)> _awaitingKeys = [];

    /// <summary>Guards the fix-up save from being intercepted as a change of its own.</summary>
    private bool _linkingKeys;

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

    /// <summary>
    /// Links each insert's audit row to the key the database has just assigned it.
    /// </summary>
    /// <remarks>
    /// <b>Why a second statement is unavoidable.</b> The change set has to be read before the save
    /// - afterwards the original values are gone - and the key only exists after it, because
    /// PostgreSQL assigns it and returns it with the insert. The two facts are available at
    /// different moments, so something has to span them.
    /// <para>
    /// What is <em>not</em> deferred is the audit row itself. It is inserted with the change, in
    /// the same transaction, exactly as an update's is; only the identifier is corrected here.
    /// That keeps the guarantee this file is built on - a change and its audit row commit together
    /// or not at all - and it means a failure of this second statement degrades to an entry that
    /// exists but is not linked, rather than to a create that happened with nothing recorded.
    /// </para>
    /// </remarks>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (!LinkKeys(eventData.Context))
        {
            return base.SavedChanges(eventData, result);
        }

        _linkingKeys = true;

        try
        {
            eventData.Context!.SaveChanges();
        }
        finally
        {
            _linkingKeys = false;
        }

        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc cref="SavedChanges" />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (!LinkKeys(eventData.Context))
        {
            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        _linkingKeys = true;

        try
        {
            await eventData.Context!.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _linkingKeys = false;
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // The save rolled back, so the audit rows went with it. Left in the list they would be
        // linked - and saved - by whatever the caller tried next.
        _awaitingKeys.Clear();

        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _awaitingKeys.Clear();

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Copies the assigned keys onto the waiting audit rows.
    /// </summary>
    /// <param name="context">The context that has just saved.</param>
    /// <returns>Whether anything needs saving as a result.</returns>
    private bool LinkKeys(DbContext? context)
    {
        if (context is null || _awaitingKeys.Count == 0)
        {
            return false;
        }

        var pending = _awaitingKeys.ToList();

        // Cleared before the save rather than after, so the fix-up save's own completion callback
        // finds nothing to do and cannot recurse.
        _awaitingKeys.Clear();

        var linked = false;

        foreach (var (log, entity) in pending)
        {
            // Zero would mean the key was never assigned, which should be impossible for a row
            // that has just been inserted. Leaving the placeholder is the honest answer if it
            // ever happens - a "0" here is what this whole change is about.
            if (entity.Id == 0)
            {
                continue;
            }

            log.EntityId = entity.Id.ToString(CultureInfo.InvariantCulture);
            linked = true;
        }

        return linked;
    }

    private void Capture(DbContext? context)
    {
        // The fix-up save carries nothing but corrected identifiers on rows this interceptor
        // wrote a moment ago. Capturing it would be auditing the audit trail.
        if (context is null || _linkingKeys)
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

        // A save with nothing pending from a previous one. Anything still here belongs to a unit
        // of work that never completed.
        _awaitingKeys.Clear();

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

            var log = new AuditLog
            {
                TenantId = entry.Entity.TenantId,
                UserId = userId,
                EntityName = entry.Entity.GetType().Name,

                // An insert has no key yet - the database assigns it and returns it with the
                // insert, which has not run. Written as a placeholder here and corrected in
                // SavedChanges; see LinkKeys. Recording entry.Entity.Id at this moment wrote the
                // uninitialised zero, which is why no record's history ever showed its own
                // creation.
                //
                // Invariant culture for the others, not the ambient one. An audit trail is matched
                // and searched by this string, and a culture using non-ASCII digits would write an
                // id that no longer equals the one written under any other culture.
                EntityId = action == AuditAction.Created
                    ? PendingEntityId
                    : entry.Entity.Id.ToString(CultureInfo.InvariantCulture),
                Action = action,
                Changes = JsonSerializer.Serialize(changes, SerializerOptions),
                CorrelationId = _requestContext.CorrelationId,
                IpAddress = _requestContext.IpAddress,
                OccurredOn = utcNow,
            };

            logs.Add(log);

            if (action == AuditAction.Created)
            {
                // The entity, not a copy of its id: the id is the thing that does not exist yet.
                _awaitingKeys.Add((log, entry.Entity));
            }
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
