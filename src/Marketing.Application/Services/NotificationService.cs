using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
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
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public NotificationService(
        IRepository<Notification> notifications,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        ITenantContext tenantContext)
    {
        _notifications = notifications;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppNotification>> GetAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;

        var rows = await _queries.ToListAsync(
            Scoped(userId)
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
        var mine = Scoped(userId);

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

    private static AppNotification Map(Notification notification) =>
        new(
            PublicId.From(PublicId.Notification, notification.Id),
            notification.Kind,
            notification.Title,
            notification.Body,
            notification.Priority,
            notification.Icon,
            notification.Read,
            notification.ActionLabel,
            notification.ActionRoute,
            notification.OccurredOn);
}
