using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>
/// Which WhatsApp numbers one person may use, and for what.
/// </summary>
/// <remarks>
/// Resolved once and then asked many times, so a list of fifty conversations costs one query for
/// access rather than fifty. An administrator's scope is unrestricted and carries no rows at all:
/// their access comes from their role, and rows written for them would only ever disagree with it.
/// </remarks>
public sealed class WhatsAppAccessScope
{
    private static readonly WhatsAppAccessLevel[] Everything =
        [WhatsAppAccessLevel.View, WhatsAppAccessLevel.Reply, WhatsAppAccessLevel.Broadcast];

    private readonly Dictionary<long, WhatsAppAccessLevel[]> _grants;

    private WhatsAppAccessScope(bool isUnrestricted, Dictionary<long, WhatsAppAccessLevel[]> grants)
    {
        IsUnrestricted = isUnrestricted;
        _grants = grants;
    }

    /// <summary>Every permission on every number: administrators, platform staff and background jobs.</summary>
    public static WhatsAppAccessScope Unrestricted { get; } = new(true, []);

    /// <summary>Whether this scope ignores per-number access entirely.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>
    /// The numbers this person may view. Meaningless when <see cref="IsUnrestricted"/> is set, which
    /// every query has to check first - an empty list there means "all", not "none".
    /// </summary>
    public IReadOnlyList<long> ViewableAccountIds =>
        [.. _grants.Where(entry => entry.Value.Contains(WhatsAppAccessLevel.View)).Select(entry => entry.Key)];

    /// <summary>Builds a restricted scope from stored access rows.</summary>
    /// <param name="rows">The person's rows.</param>
    public static WhatsAppAccessScope From(IEnumerable<WhatsAppAccountAccess> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var grants = new Dictionary<long, WhatsAppAccessLevel[]>();

        foreach (var row in rows)
        {
            // View is implied by nothing. A row with reply but not view cannot be written through the
            // API, and one that got here any other way grants nothing rather than a reply into a thread
            // the person cannot read.
            if (!row.CanView)
            {
                continue;
            }

            grants[row.WhatsAppConnectionId] =
            [
                WhatsAppAccessLevel.View,
                .. row.CanReply ? [WhatsAppAccessLevel.Reply] : Array.Empty<WhatsAppAccessLevel>(),
                .. row.CanBroadcast ? [WhatsAppAccessLevel.Broadcast] : Array.Empty<WhatsAppAccessLevel>(),
            ];
        }

        return new WhatsAppAccessScope(false, grants);
    }

    /// <summary>Whether this person may do one thing on one number.</summary>
    /// <param name="accountId">Internal connection key.</param>
    /// <param name="permission">What they want to do.</param>
    public bool Allows(long accountId, WhatsAppAccessLevel permission) =>
        IsUnrestricted || (_grants.TryGetValue(accountId, out var granted) && granted.Contains(permission));

    /// <summary>Everything this person may do on one number, in a fixed order.</summary>
    /// <param name="accountId">Internal connection key.</param>
    public IReadOnlyList<WhatsAppAccessLevel> PermissionsOn(long accountId) =>
        IsUnrestricted ? Everything : _grants.TryGetValue(accountId, out var granted) ? granted : [];
}

/// <summary>
/// Resolves WhatsApp numbers for a caller and enforces per-number access.
/// </summary>
/// <remarks>
/// The single place the two permission layers meet. The global permission is checked by the route
/// attribute before any of this runs; this adds "and on this number".
/// </remarks>
public interface IWhatsAppAccessService
{
    /// <summary>The signed-in caller's access.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccessScope> GetCallerScopeAsync(CancellationToken cancellationToken = default);

    /// <summary>Another member's access, for validating an assignment or addressing a realtime event.</summary>
    /// <param name="userId">Internal user key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccessScope> GetScopeForUserAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the number a request names, or the workspace default when it names none, and demands
    /// the permission the action needs on it.
    /// </summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="required">What the caller is about to do.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracked connection, or null when the workspace has no number and none was named.</returns>
    /// <exception cref="NotFoundException">
    /// The id names no number in this workspace, or one the caller may not even see.
    /// </exception>
    /// <exception cref="ForbiddenException">The caller may see the number but not do this on it.</exception>
    public Task<WhatsAppConnection?> ResolveAsync(
        string? accountId,
        WhatsAppAccessLevel required,
        CancellationToken cancellationToken = default);

    /// <summary>Refuses an action the scope does not allow on a number, naming the number.</summary>
    /// <param name="scope">The caller's access.</param>
    /// <param name="accountId">Internal connection key.</param>
    /// <param name="label">What the workspace calls the number, for the message.</param>
    /// <param name="required">What the caller is about to do.</param>
    /// <exception cref="ForbiddenException">The scope does not allow it.</exception>
    public void Demand(WhatsAppAccessScope scope, long accountId, string label, WhatsAppAccessLevel required);

    /// <summary>
    /// Active members who may read a number's conversations: an administrator, or someone holding
    /// both the inbox permission and view on that number.
    /// </summary>
    /// <param name="accountId">Internal connection key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<long>> UsersWhoMayViewAsync(long accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the workspace calls each of its numbers, deleted ones included - history outlives the
    /// number it was written to.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyDictionary<long, string>> LabelsAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppAccessService" />
public sealed class WhatsAppAccessService : IWhatsAppAccessService
{
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IRepository<WhatsAppAccountAccess> _access;
    private readonly IRepository<User> _users;
    private readonly IQueryExecutor _queries;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;

    private WhatsAppAccessScope? _callerScope;
    // Keyed by tenant: the scheduler walks several workspaces inside one dependency-injection scope,
    // and one workspace's labels must never name another's numbers.
    private readonly Dictionary<long, IReadOnlyDictionary<long, string>> _labels = [];

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppAccessService(
        IWhatsAppConnectionRepository connections,
        IRepository<WhatsAppAccountAccess> access,
        IRepository<User> users,
        IQueryExecutor queries,
        ICurrentUser currentUser,
        ITenantContext tenantContext)
    {
        _connections = connections;
        _access = access;
        _users = users;
        _queries = queries;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<WhatsAppAccessScope> GetCallerScopeAsync(CancellationToken cancellationToken = default)
    {
        if (_callerScope is not null)
        {
            return _callerScope;
        }

        // No user is a background job acting for the workspace as a whole; platform staff and
        // administrators hold every number by role.
        if (_currentUser.UserId is not { } userId
            || _currentUser.IsSuperAdmin
            || _currentUser.IsInRole(Roles.Admin)
            || _currentUser.IsInRole(Roles.SuperAdmin))
        {
            return _callerScope = WhatsAppAccessScope.Unrestricted;
        }

        return _callerScope = await LoadRowsAsync(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppAccessScope> GetScopeForUserAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var adminRole = Roles.Normalise(Roles.Admin);

        var isAdmin = await _queries.CountAsync(
            _users.Query().Where(user =>
                user.Id == userId
                && user.UserRoles.Any(assignment => !assignment.IsDeleted && assignment.Role.NormalizedName == adminRole)),
            cancellationToken) > 0;

        return isAdmin ? WhatsAppAccessScope.Unrestricted : await LoadRowsAsync(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnection?> ResolveAsync(
        string? accountId,
        WhatsAppAccessLevel required,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var scope = await GetCallerScopeAsync(cancellationToken);

        WhatsAppConnection? connection;

        if (string.IsNullOrWhiteSpace(accountId))
        {
            connection = await _connections.FindForTenantAsync(tenantId, cancellationToken);

            if (connection is null)
            {
                return null;
            }
        }
        else
        {
            var id = PublicId.Parse(PublicId.WhatsAppAccount, accountId, "WhatsApp account");

            connection = await _connections.FindByIdForTenantAsync(tenantId, id, cancellationToken);

            // A number the caller may not see is reported exactly as one that does not exist, so its
            // existence cannot be probed by someone without access to it.
            if (connection is null || !scope.Allows(connection.Id, WhatsAppAccessLevel.View))
            {
                throw new NotFoundException("WhatsApp account", accountId);
            }
        }

        Demand(scope, connection.Id, connection.Label, required);

        return connection;
    }

    /// <inheritdoc />
    public void Demand(WhatsAppAccessScope scope, long accountId, string label, WhatsAppAccessLevel required)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.Allows(accountId, required))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(label) ? "this WhatsApp number" : label;

        throw new ForbiddenException(
            "whatsapp_account_forbidden",
            $"You don't have {required.ToString().ToLowerInvariant()} access to {name}. "
            + "Ask an admin to give you access to this number.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> UsersWhoMayViewAsync(
        long accountId,
        CancellationToken cancellationToken = default)
    {
        var granted = await _queries.ToListAsync(
            _access.Query()
                .Where(row => row.WhatsAppConnectionId == accountId && row.CanView)
                .Select(row => row.UserId),
            cancellationToken);

        var members = await _queries.ToListAsync(
            _users.Query()
                .Where(user => user.Status == AppConstants.UserStatus.Active)
                .Select(user => new
                {
                    user.Id,
                    Roles = user.UserRoles.Where(assignment => !assignment.IsDeleted)
                        .Select(assignment => assignment.Role.Name).ToList(),
                    Overrides = user.PermissionOverrides.Where(entry => !entry.IsDeleted)
                        .Select(entry => new { entry.Permission, entry.IsGranted }).ToList(),
                }),
            cancellationToken);

        var adminRole = Roles.Normalise(Roles.Admin);

        return
        [
            .. members
                .Where(member =>
                    member.Roles.Any(role => Roles.Normalise(role) == adminRole)
                    || (granted.Contains(member.Id)
                        && EffectivePermissions.Resolve(
                                member.Roles,
                                member.Overrides.Select(entry => new UserPermissionOverride
                                {
                                    Permission = entry.Permission,
                                    IsGranted = entry.IsGranted,
                                }))
                            .Contains(Permissions.WhatsApp.InboxView, StringComparer.Ordinal)))
                .Select(member => member.Id),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> LabelsAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();

        if (!_labels.TryGetValue(tenantId, out var labels))
        {
            _labels[tenantId] = labels = await _connections.LabelsForTenantAsync(tenantId, cancellationToken);
        }

        return labels;
    }

    private async Task<WhatsAppAccessScope> LoadRowsAsync(long userId, CancellationToken cancellationToken) =>
        WhatsAppAccessScope.From(await _queries.ToListAsync(
            _access.Query().Where(row => row.UserId == userId),
            cancellationToken));
}
