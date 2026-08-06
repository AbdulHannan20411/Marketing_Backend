using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Application.Services;

/// <inheritdoc cref="ITenantScopeResolver" />
public sealed partial class TenantScopeResolver : ITenantScopeResolver
{
    private readonly IUserRepository _users;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantScopeResolver> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="users">User repository, used to resolve the admin account.</param>
    /// <param name="currentUser">The authenticated principal.</param>
    /// <param name="tenantContext">Ambient tenant.</param>
    /// <param name="logger">Logger.</param>
    public TenantScopeResolver(
        IUserRepository users,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        ILogger<TenantScopeResolver> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IDisposable> EnterAsync(string? adminId, CancellationToken cancellationToken = default)
    {
        // Ignored rather than rejected for non-platform callers. Erroring would tell an ordinary
        // user that the parameter is meaningful, which is a small but free piece of reconnaissance.
        if (string.IsNullOrWhiteSpace(adminId) || !_currentUser.IsSuperAdmin)
        {
            return NullScope.Instance;
        }

        if (!PublicId.TryParse(PublicId.AdminAccount, adminId, out var accountId))
        {
            throw new ForbiddenException("The requested account could not be resolved.");
        }

        // adminId names an account, not a tenant. The tenant is read from that account's own row,
        // so a Super Admin cannot address a tenant that has no administrator.
        var account = await _users.FindWithRolesAsync(accountId, cancellationToken);

        if (account?.TenantId is not { } tenantId)
        {
            throw new ForbiddenException("The requested account could not be resolved.");
        }

        LogScopeEntered(_currentUser.UserId ?? Guid.Empty, accountId, tenantId);

        return _tenantContext.BeginScope(tenantId, account.Tenant?.Slug);
    }

    /// <summary>
    /// Recorded on every use, because acting as another organisation is exactly the action a
    /// compliance review asks to see evidence of.
    /// </summary>
    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Super admin {SuperAdminId} is acting on admin account {AdminAccountId} in tenant {TenantId}.")]
    private partial void LogScopeEntered(Guid superAdminId, Guid adminAccountId, Guid tenantId);

    /// <summary>A scope that changes nothing, returned when the caller keeps their own tenant.</summary>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // Nothing was entered, so nothing needs restoring.
        }
    }
}
