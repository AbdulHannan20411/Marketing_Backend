using System.Text.Json;
using Marketing.Application.DTOs.Audit;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.Services.Audit;

/// <summary>What happened to one record, and who did it.</summary>
public interface IRecordHistoryService
{
    /// <summary>
    /// Reads one record's history, newest first.
    /// </summary>
    /// <remarks>
    /// Authorisation is about the record, never about the audit rows: a caller who may not open the
    /// record may not read what happened to it either, and a record in another workspace does not
    /// exist as far as this endpoint is concerned.
    /// </remarks>
    /// <param name="entityName">Public record type, as the registry names it.</param>
    /// <param name="entityId">Public record identifier.</param>
    /// <param name="query">Paging and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotFoundException">Unknown record type, malformed id, or a record the caller cannot see.</exception>
    /// <exception cref="ForbiddenException">The caller lacks the permission that guards the record.</exception>
    public Task<PagedResult<RecordHistoryEntry>> GetAsync(
        string entityName,
        string entityId,
        RecordHistoryQuery query,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRecordHistoryService" />
public sealed class RecordHistoryService : IRecordHistoryService
{
    private readonly IAuditLogRepository _audit;
    private readonly IAuditableRecordLocator _records;
    private readonly IUserRepository _users;
    private readonly IQueryExecutor _queries;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="audit">Audit entries, read-only.</param>
    /// <param name="records">Answers whether the record itself is one the caller could open.</param>
    /// <param name="users">Used to put a name to each change.</param>
    /// <param name="queries">Query executor.</param>
    /// <param name="currentUser">Caller, for the permission check.</param>
    /// <param name="tenantContext">Ambient workspace, which platform staff may be outside of.</param>
    public RecordHistoryService(
        IAuditLogRepository audit,
        IAuditableRecordLocator records,
        IUserRepository users,
        IQueryExecutor queries,
        ICurrentUser currentUser,
        ITenantContext tenantContext)
    {
        _audit = audit;
        _records = records;
        _users = users;
        _queries = queries;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<PagedResult<RecordHistoryEntry>> GetAsync(
        string entityName,
        string entityId,
        RecordHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // In this order, and the order matters: an unknown name must not tell a caller whether the
        // permission they lack exists, and a permission failure must not confirm a record's id.
        var registered = AuditableEntities.Find(entityName)
                         ?? throw new NotFoundException("Record type", entityName ?? "(none)");

        if (registered.PlatformOnly && !_currentUser.IsSuperAdmin)
        {
            throw new ForbiddenException("forbidden", "Only platform staff may read this record's history.");
        }

        if (registered.Permission is { Length: > 0 } permission
            && !_currentUser.IsSuperAdmin
            && !_currentUser.HasPermission(permission))
        {
            throw new ForbiddenException(
                "forbidden",
                "You do not have permission to read this record, so its history is not yours to read either.");
        }

        // Platform staff looking at a workspace do it through ?adminId=, which enters that
        // workspace and makes this an ordinary scoped read. Without it they are outside every
        // workspace, and a scoped read has no tenant to be scoped to.
        var crossTenant = registered.PlatformOnly || (_currentUser.IsSuperAdmin && !_tenantContext.HasTenant);

        // Resolving the identifier is also the existence check: every path below either finds the
        // caller's own row or finds nothing, and finding nothing is a 404 rather than an empty page.
        var id = await ResolveAsync(registered, entityId, crossTenant, cancellationToken)
                 ?? throw new NotFoundException(registered.PublicName, entityId ?? "(none)");

        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var matching = _audit.Query()
            .Where(entry => entry.EntityName == registered.EntityName && entry.EntityId == key);

        if (query.Action is { } action)
        {
            matching = matching.Where(entry => entry.Action == action);
        }

        if (query.UserId is { Length: > 0 } userId)
        {
            matching = matching.Where(entry => entry.UserId == ParseActor(userId));
        }

        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            matching = matching.Where(entry => entry.OccurredOn >= start);
        }

        if (query.To is { } to)
        {
            // The whole of the closing day, not the instant it begins: a filter that excluded
            // everything after midnight on the day the user picked would look like data loss.
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            matching = matching.Where(entry => entry.OccurredOn < end);
        }

        var total = await _queries.CountAsync(matching, cancellationToken);

        // Clamped here rather than on the request type, whose own ceiling is the platform's
        // hundred: this endpoint's rows are wider than most.
        var size = Math.Min(query.PageSize, RecordHistoryQuery.PanelMaxPageSize);

        var rows = await _queries.ToListAsync(
            matching
                .OrderByDescending(entry => entry.OccurredOn)
                .ThenByDescending(entry => entry.Id)
                .Skip((query.Page - 1) * size)
                .Take(size)
                .Select(entry => new
                {
                    entry.Id,
                    entry.UserId,
                    entry.Action,
                    entry.Changes,
                    entry.OccurredOn,
                }),
            cancellationToken);

        var names = await NamesAsync([.. rows.Select(row => row.UserId).Distinct()], cancellationToken);

        return new PagedResult<RecordHistoryEntry>(
            [
                .. rows.Select(row =>
                {
                    // The system identity is a real row, but naming it tells a reader nothing. The
                    // client shows "Automatic" for a null actor.
                    var byPerson = row.UserId != Platform.SystemUserId;

                    return new RecordHistoryEntry(
                        PublicId.From(PublicId.Audit, row.Id),
                        registered.PublicName,
                        entityId,
                        row.Action,
                        byPerson ? PublicId.From(PublicId.Employee, row.UserId) : null,
                        byPerson ? names.GetValueOrDefault(row.UserId) : null,
                        row.OccurredOn,
                        Parse(row.Changes));
                }),
            ],
            total,
            query.Page,
            size);
    }

    /// <summary>
    /// Turns what the client sent into the row id the audit trail was written against.
    /// </summary>
    /// <remarks>
    /// Three ways of naming a record, because the rest of the API already names them three ways:
    /// most by public id, the one-per-workspace records as <c>current</c>, and the email templates
    /// by the key every other route uses for them. Whichever it is, the record itself is loaded -
    /// an audit row's own tenant id is the caller's claim about where they were, not proof.
    /// </remarks>
    private async Task<long?> ResolveAsync(
        AuditableEntity registered,
        string? entityId,
        bool crossTenant,
        CancellationToken cancellationToken)
    {
        var current = string.Equals(entityId, AuditableRecord.Current, StringComparison.OrdinalIgnoreCase);

        // The workspace itself answers for its own identity: a tenant row has no tenant column to
        // be scoped by, and asking the database whether workspace 15 is workspace 15 would be a
        // query that cannot be written. A member may read their own; platform staff may read any.
        if (registered.EntityName == nameof(DataAccess.Entities.Tenant))
        {
            var wanted = current
                ? _tenantContext.TenantId
                : PublicId.TryParse(registered.IdPrefix, entityId, out var asked) ? asked : null;

            return wanted is { } workspace
                   && (_currentUser.IsSuperAdmin || workspace == _tenantContext.TenantId)
                ? workspace
                : null;
        }

        if (registered.SingleRowPerTenant && current)
        {
            // One row per workspace, which does not exist until somebody saves one: an auto-reply
            // settings row is written the first time the screen is saved, and before that there is
            // nothing to have a history.
            return await _records.FindForTenantAsync(registered.EntityName, cancellationToken);
        }

        if (registered.KeyColumn is { Length: > 0 } keyColumn)
        {
            return entityId is { Length: > 0 } key
                ? await _records.FindByKeyAsync(
                    registered.EntityName, keyColumn, key, crossTenant, cancellationToken)
                : null;
        }

        // Not a 422: an id of the wrong kind - a contact's id asked for as a template - is the same
        // mistake as a record that is not there, and answering "invalid" would tell a caller which
        // prefixes the platform uses.
        if (!PublicId.TryParse(registered.IdPrefix, entityId, out var id))
        {
            return null;
        }

        return await _records.ExistsAsync(registered.EntityName, id, crossTenant, cancellationToken)
            ? id
            : null;
    }

    /// <summary>
    /// Puts a name to each actor on the page.
    /// </summary>
    /// <remarks>
    /// Soft-deleted users included, on purpose: history that reads "by ?" once somebody leaves the
    /// company is worse than no history at all, and the name is already in every row they wrote.
    /// </remarks>
    private async Task<Dictionary<long, string>> NamesAsync(
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var rows = await _queries.ToListAsync(
            _users.Query()
                .IgnoreQueryFilters()
                .Where(user => userIds.Contains(user.Id))
                .Select(user => new { user.Id, user.DisplayName }),
            cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.DisplayName);
    }

    /// <summary>Reads an actor filter, which the client sends as a public id.</summary>
    /// <remarks>
    /// Employees and platform administrators share the audit table, so either prefix is accepted. A value
    /// that is neither parses as nothing and matches nothing, rather than failing the request:
    /// a filter is a narrowing, and the honest answer to an impossible one is an empty page.
    /// </remarks>
    private static long ParseActor(string userId)
    {
        foreach (var prefix in (string[])[PublicId.Employee, PublicId.AdminAccount])
        {
            if (PublicId.TryParse(prefix, userId, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    /// <summary>
    /// Hands the stored change set back exactly as it was written.
    /// </summary>
    /// <remarks>
    /// Parsed rather than re-serialised, so nothing is added, renamed or "filled in" on the way
    /// out: the panel shows the fields that changed and no others, which is only true if this
    /// method leaves the document alone.
    /// </remarks>
    private static object Parse(string? changes)
    {
        if (changes is not { Length: > 0 })
        {
            return new Dictionary<string, object>();
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(changes);
        }
        catch (JsonException)
        {
            // A row written by an older build, or by hand. Better an empty change set than a 500
            // on a history panel.
            return new Dictionary<string, object>();
        }
    }
}
