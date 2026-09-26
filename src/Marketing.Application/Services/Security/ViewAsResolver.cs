using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Marketing.Application.Services.Security;

/// <summary>Enters a read-only preview of what one teammate can see.</summary>
public interface IViewAsResolver
{
    /// <summary>
    /// Enters a preview of <paramref name="viewAsEmployeeId"/> for the rest of the request.
    /// </summary>
    /// <remarks>
    /// Returns a scope that does nothing when the parameter is absent, so a caller can wrap every
    /// request in it without branching.
    /// </remarks>
    /// <param name="viewAsEmployeeId">Public employee id, or null to stay yourself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ForbiddenException">The caller does not administer this workspace.</exception>
    /// <exception cref="NotFoundException">
    /// No such teammate in the caller's workspace - including one who exists in another.
    /// </exception>
    public Task<IDisposable> EnterAsync(string? viewAsEmployeeId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IViewAsResolver" />
public sealed partial class ViewAsResolver : IViewAsResolver
{
    /// <summary>
    /// How long one entry into a preview covers, for the activity entry.
    /// </summary>
    /// <remarks>
    /// The parameter rides every request, and an entry per request would bury the workspace's
    /// activity feed under one administrator's browsing. This records the act of looking, once,
    /// and lets the following few hundred reads share it.
    /// </remarks>
    private static readonly TimeSpan RecordOncePer = TimeSpan.FromMinutes(30);

    private readonly IUserRepository _users;
    private readonly IRepository<ActivityEntry> _activity;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IViewAsContext _viewAs;
    private readonly IMemoryCache _recentlyRecorded;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ViewAsResolver> _logger;

    /// <summary>Initialises a new instance.</summary>
    public ViewAsResolver(
        IUserRepository users,
        IRepository<ActivityEntry> activity,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IViewAsContext viewAs,
        IMemoryCache recentlyRecorded,
        IDateTimeProvider clock,
        ILogger<ViewAsResolver> logger)
    {
        _users = users;
        _activity = activity;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _viewAs = viewAs;
        _recentlyRecorded = recentlyRecorded;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IDisposable> EnterAsync(
        string? viewAsEmployeeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewAsEmployeeId))
        {
            return NullScope.Instance;
        }

        var caller = _currentUser.UserId
                     ?? throw new AuthenticationException("not_authenticated");

        // Administering the workspace is the whole permission. There is no finer grant, because
        // there is no coherent middle: being able to see what one teammate sees is being able to
        // see what the workspace's data looks like from inside, which is what an administrator
        // already has by other means.
        if (!_currentUser.IsInRole(Roles.Admin) && !_currentUser.IsSuperAdmin)
        {
            throw new ForbiddenException(
                "forbidden",
                "Only an administrator of this workspace can preview what a teammate sees.");
        }

        // Not 403. An id from another workspace and an id that never existed are the same answer,
        // or the difference between them tells an administrator which ids are real elsewhere.
        if (!PublicId.TryParse(PublicId.Employee, viewAsEmployeeId, out var employeeId))
        {
            throw new NotFoundException("Employee", viewAsEmployeeId);
        }

        var employee = await _users.FindWithRolesAsync(employeeId, cancellationToken);

        // FindWithRolesAsync ignores the tenant filter, because token issuance needs it to. So the
        // workspace check is made here, explicitly, rather than assumed from the query.
        if (employee?.TenantId is not { } workspace
            || workspace != _tenantContext.TenantId
            || _tenantContext.TenantId is null)
        {
            throw new NotFoundException("Employee", viewAsEmployeeId);
        }

        var roles = employee.UserRoles
            .Select(userRole => userRole.Role.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Platform staff are not previewable. They hold no workspace, so the check above should
        // already have refused them - this is the belt to that braces, because the one thing this
        // feature must never do is hand somebody a wider view than they arrived with.
        if (roles.Contains(Roles.SuperAdmin, StringComparer.Ordinal))
        {
            throw new NotFoundException("Employee", viewAsEmployeeId);
        }

        var identity = new ViewAsIdentity(
            employee.Id,
            employee.DisplayName,
            roles,

            // Resolved exactly as the teammate's own token is - roles plus overrides - and then
            // narrowed to what the caller already holds.
            Narrowed(EffectivePermissions.Resolve(roles, employee.PermissionOverrides)));

        // Everything above this line still sees the real caller - Narrowed intersects against
        // their permissions, and the entry below is attributed to them. The scope is entered last,
        // deliberately: entering it first would have the resolver check the preview against
        // itself, which is a check that always passes.
        await RecordAsync(caller, employee, cancellationToken);

        LogPreviewEntered(caller, employee.Id, workspace);

        return _viewAs.BeginScope(identity);
    }

    /// <summary>
    /// Intersects the teammate's permissions with the caller's own.
    /// </summary>
    /// <remarks>
    /// So that a preview can only ever subtract. Without it, an administrator previewing a
    /// colleague who holds a permission they do not - an override granted to one administrator and
    /// not another - would acquire it for the length of the request, and "view as" would be a
    /// privilege escalation wearing the clothes of a diagnostic.
    /// <para>
    /// In practice this changes nothing: an administrator holds everything their workspace grants.
    /// It is here for the case where that stops being true.
    /// </para>
    /// </remarks>
    /// <param name="theirs">The teammate's effective permissions.</param>
    private IReadOnlyCollection<string> Narrowed(IEnumerable<string> theirs) =>
        [.. theirs.Where(_currentUser.HasPermission)];

    /// <summary>
    /// Records the act of looking, on the workspace's own activity feed.
    /// </summary>
    /// <remarks>
    /// On the feed the workspace can read rather than only in a log file, because the point of
    /// recording it is that the person being previewed can find out. Written once per entry - see
    /// <see cref="RecordOncePer"/> - and deduplicated in memory, so a second instance of the API
    /// would write a second entry for the same sitting. Duplicated is the right way for this to
    /// fail: an audit trail may repeat itself, and must not go quiet.
    /// </remarks>
    /// <param name="caller">The administrator.</param>
    /// <param name="employee">The teammate being previewed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task RecordAsync(long caller, User employee, CancellationToken cancellationToken)
    {
        var key = $"view-as:{caller}:{employee.Id}:{_currentUser.SessionId}";

        if (_recentlyRecorded.TryGetValue(key, out _))
        {
            return;
        }

        _recentlyRecorded.Set(key, true, RecordOncePer);

        _activity.Add(new ActivityEntry
        {
            Actor = _currentUser.DisplayName ?? "An administrator",
            Action = "viewed the app as",
            Subject = employee.DisplayName,
            OccurredOn = _clock.UtcNow,
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Recorded on every request, not only on entry.
    /// </summary>
    /// <remarks>
    /// The activity feed carries the readable summary; this carries the complete trail, for the
    /// question the summary cannot answer - which is not "did they look" but "how long for".
    /// </remarks>
    [LoggerMessage(
        EventId = 2210,
        Level = LogLevel.Information,
        Message = "Administrator {AdministratorId} is previewing employee {EmployeeId} in tenant {TenantId}, read-only.")]
    private partial void LogPreviewEntered(long administratorId, long employeeId, long tenantId);

    /// <summary>A scope that changes nothing, returned when the caller is simply themselves.</summary>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // Nothing was entered, so nothing needs restoring.
        }
    }
}
