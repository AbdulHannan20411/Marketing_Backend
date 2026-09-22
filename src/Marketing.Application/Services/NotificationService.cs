using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="INotificationService" />
public sealed class NotificationService : INotificationService
{
    /// <summary>
    /// Ceiling on what one request returns.
    /// <para>
    /// The client caps the topbar dropdown at five and the centre shows the rest. Fifty is enough
    /// for both without paging, and bounds the payload for a noisy tenant.
    /// </para>
    /// </summary>
    private const int MaxNotifications = 50;

    private readonly IRepository<Notification> _notifications;
    private readonly IRepository<UserNotificationPreference> _preferences;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public NotificationService(
        IRepository<Notification> notifications,
        IRepository<UserNotificationPreference> preferences,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        ITenantContext tenantContext)
    {
        _notifications = notifications;
        _preferences = preferences;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppNotification>> GetAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;

        var silenced = await SilencedKindsAsync(userId, cancellationToken);

        var rows = await _queries.ToListAsync(
            Scoped(userId)
                .Where(notification => !silenced.Contains(notification.Kind))
                .OrderByDescending(notification => notification.OccurredOn)
                .Take(MaxNotifications),
            cancellationToken);

        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<NotificationFeed> GetPageAsync(
        NotificationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var userId = _currentUser.UserId;

        // Applied to the rows and to both counters. A silenced category that still counted towards
        // the bell would be the switch's most obvious lie, and the client cannot correct it: the
        // two numbers below are computed here, not from the page.
        var silenced = await SilencedKindsAsync(userId, cancellationToken);

        var mine = Scoped(userId).Where(notification => !silenced.Contains(notification.Kind));

        var size = Math.Clamp(query.PageSize ?? NotificationQuery.DefaultPageSize, 1, NotificationQuery.MaxPageSize);
        var page = Math.Max(query.Page ?? 1, 1);

        var matching = mine;

        if (query.UnreadOnly == true)
        {
            matching = matching.Where(notification => !notification.Read);
        }

        if (query.Priority is { } priority)
        {
            matching = matching.Where(notification => notification.Priority == priority);
        }

        if (query.Category is { } category)
        {
            // Expanded to kinds rather than matched on a stored category, so the grouping has one
            // definition and old rows written before categories existed are filed correctly too.
            var kinds = NotificationCategories.KindsIn(category);

            matching = category == NotificationCategory.System

                // System is the fallback, so it is everything that is not mapped elsewhere - which
                // cannot be listed, only excluded.
                ? matching.Where(notification =>
                    kinds.Contains(notification.Kind)
                    || !NotificationCategories.Mapped.Contains(notification.Kind))
                : matching.Where(notification => kinds.Contains(notification.Kind));
        }

        var total = await _queries.CountAsync(matching, cancellationToken);

        var rows = await _queries.ToListAsync(
            matching
                .OrderByDescending(notification => notification.OccurredOn)
                .ThenByDescending(notification => notification.Id)
                .Skip((page - 1) * size)
                .Take(size),
            cancellationToken);

        // Over everything addressed to the caller, not over the page and not over the filters: the
        // bell is a count of unread mail, and it would be wrong the moment someone filtered by
        // "critical" or turned to page two.
        var unread = await _queries.CountAsync(
            mine.Where(notification => !notification.Read),
            cancellationToken);

        var critical = await _queries.CountAsync(
            mine.Where(notification => !notification.Read && notification.Priority == NotificationPriority.Critical),
            cancellationToken);

        return new NotificationFeed(
            [.. rows.Select(Map)],
            page,
            size,
            total,
            total == 0 ? 1 : (int)Math.Ceiling(total / (double)size),
            unread,
            critical);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppNotification>> MarkReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Notification, notificationId, "notification");

        var notification = await _notifications.GetForUpdateAsync(id, cancellationToken)
                           ?? throw new NotFoundException("Notification", notificationId);

        // A caller may only mark their own, or a tenant-wide one. Without this check the id alone
        // would let anyone in the tenant clear a colleague's notification.
        if (notification.UserId is { } owner && owner != _currentUser.UserId)
        {
            throw new NotFoundException("Notification", notificationId);
        }

        notification.Read = true;
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // The whole list comes back, so the client replaces its state in one step rather than
        // patching an item and hoping its copy was current.
        return await GetAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppNotification>> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;

        var unread = await _queries.ToListAsync(
            Scoped(userId, tracked: true).Where(notification => !notification.Read),
            cancellationToken);

        foreach (var notification in unread)
        {
            notification.Read = true;
        }

        if (unread.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(cancellationToken);
    }

    /// <summary>
    /// Notifications addressed to this user, plus tenant-wide ones.
    /// <para>
    /// A null recipient means everyone in the tenant, which is how a platform-wide warning is
    /// delivered without writing one row per user.
    /// </para>
    /// </summary>
    private IQueryable<Notification> Scoped(long? userId, bool tracked = false)
    {
        // Tenancy is applied here rather than left to the global filter, because a platform
        // administrator bypasses that filter entirely. Relying on it meant "UserId is null" - which
        // means "everyone in the workspace" - matched every workspace on the platform at once, so a
        // Super Admin's bell filled with other companies' plan changes and campaign results.
        // The global filter still runs. For a tenant user it already confines rows to their
        // workspace, so only the recipient test is added here. For a platform administrator it is
        // bypassed by design, which is exactly why the constraint below has to be explicit.
        var query = _notifications.Query(asNoTracking: !tracked);

        if (_tenantContext.TenantId is { } tenantId)
        {
            return query.Where(notification =>
                notification.TenantId == tenantId
                && (notification.UserId == null || notification.UserId == userId));
        }

        // No ambient tenant: platform staff. They get what is addressed to them personally, plus
        // genuine platform-wide announcements - and nothing belonging to a customer's workspace.
        return query.Where(notification =>
            notification.UserId == userId
            || (notification.TenantId == null && notification.UserId == null));
    }

    /// <inheritdoc />
    public async Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId
                     ?? throw new AuthenticationException("not_authenticated");

        return Shape(await StoredAsync(userId, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<NotificationPreferences> UpdatePreferencesAsync(
        IReadOnlyDictionary<string, bool> wanted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        var userId = _currentUser.UserId
                     ?? throw new AuthenticationException("not_authenticated");

        var rows = await _queries.ToListAsync(
            _preferences.Query(asNoTracking: false).Where(preference => preference.UserId == userId),
            cancellationToken);

        var changed = false;

        foreach (var category in NotificationCategories.All)
        {
            // Unknown keys are ignored rather than refused: an older client may send a category
            // that no longer exists, a newer one may send a category this build has not added yet,
            // and neither is worth failing a settings save over.
            if (!wanted.TryGetValue(Wire(category), out var enabled))
            {
                continue;
            }

            // The two that cannot be silenced are stored as on whatever the body says, so a client
            // that sends them - and the frontend sends all six - cannot write a row that would
            // later be read as "this person asked not to be warned about a stolen sign-in".
            if (!NotificationCategories.CanBeSilenced(category))
            {
                enabled = true;
            }

            var row = rows.FirstOrDefault(preference => preference.Category == category);

            if (row is null)
            {
                // Nothing is written for a switch left on: absent already means enabled.
                if (enabled)
                {
                    continue;
                }

                _preferences.Add(new UserNotificationPreference
                {
                    UserId = userId,
                    Category = category,
                    Enabled = false,
                });

                changed = true;

                continue;
            }

            if (row.Enabled != enabled)
            {
                row.Enabled = enabled;
                changed = true;
            }
        }

        if (changed)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Shape(await StoredAsync(userId, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> WhoWantsAsync(
        IReadOnlyCollection<long> userIds,
        NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var category = NotificationCategories.Of(kind);

        if (userIds.Count == 0 || !NotificationCategories.CanBeSilenced(category))
        {
            return [.. userIds];
        }

        // One query for the whole audience: this runs on the inbound-message path, which is as hot
        // as anything in the product.
        var silenced = await _queries.ToListAsync(
            _preferences.Query()
                .Where(preference =>
                    userIds.Contains(preference.UserId)
                    && preference.Category == category
                    && !preference.Enabled)
                .Select(preference => preference.UserId),
            cancellationToken);

        return silenced.Count == 0
            ? [.. userIds]
            : [.. userIds.Where(userId => !silenced.Contains(userId))];
    }

    /// <summary>The wire name of a category, which is what the client sends.</summary>
    private static string Wire(NotificationCategory category) =>
        category.ToString().ToLowerInvariant();

    /// <summary>Turns the stored answers into the six-field object the contract specifies.</summary>
    private static NotificationPreferences Shape(Dictionary<NotificationCategory, bool> stored) =>
        new(
            stored[NotificationCategory.Messages],
            stored[NotificationCategory.Campaigns],
            stored[NotificationCategory.Team],
            stored[NotificationCategory.Billing],
            Security: true,
            System: true);

    /// <summary>
    /// Reads the caller's switches, defaulting to on.
    /// </summary>
    /// <remarks>
    /// A missing row means enabled, so nothing is written until somebody actually turns something
    /// off, and a category added later starts on for everyone without a backfill.
    /// </remarks>
    private async Task<Dictionary<NotificationCategory, bool>> StoredAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var rows = await _queries.ToListAsync(
            _preferences.Query().Where(preference => preference.UserId == userId),
            cancellationToken);

        var stored = new Dictionary<NotificationCategory, bool>();

        foreach (var category in NotificationCategories.All)
        {
            var row = rows.FirstOrDefault(preference => preference.Category == category);

            // Security and system are never honoured as switches, whatever a stale row says.
            stored[category] = !NotificationCategories.CanBeSilenced(category)
                               || row is null
                               || row.Enabled;
        }

        return stored;
    }

    /// <summary>The kinds this user has asked not to hear about. Empty for almost everyone.</summary>
    private async Task<IReadOnlyList<NotificationKind>> SilencedKindsAsync(
        long? userId,
        CancellationToken cancellationToken)
    {
        if (userId is not { } id)
        {
            return [];
        }

        var stored = await StoredAsync(id, cancellationToken);

        return
        [
            .. stored
                .Where(entry => !entry.Value)
                .SelectMany(entry => NotificationCategories.KindsIn(entry.Key)),
        ];
    }

    private static AppNotification Map(Notification notification) =>
        new(
            PublicId.From(PublicId.Notification, notification.Id),
            notification.Kind,
            NotificationCategories.Of(notification.Kind),
            notification.Title,
            notification.Body,
            notification.Priority,
            notification.Icon,
            notification.Read,
            notification.ActionLabel,
            notification.ActionRoute,
            notification.OccurredOn);
}
