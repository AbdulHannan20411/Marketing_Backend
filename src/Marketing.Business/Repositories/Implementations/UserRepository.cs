using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Marketing.Common.Constants;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Entity Framework implementation of <see cref="IUserRepository"/>.</summary>
public sealed class UserRepository : Repository<User>, IUserRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public UserRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public Task<User?> FindForAuthenticationAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default) =>
        Set
            // Sign-in happens before a tenant exists on the request, so the tenant filter would
            // exclude every candidate. Bypassing it here is the single audited exception; the
            // soft-delete predicate is reapplied by hand so deleted accounts stay unusable.
            .IgnoreQueryFilters()
            .Where(user => !user.IsDeleted && user.NormalizedEmail == normalizedEmail)
            .Include(user => user.Tenant)
            .Include(user => user.UserRoles.Where(userRole => !userRole.IsDeleted))
                .ThenInclude(userRole => userRole.Role)
            // Overrides come along, because the effective grant is what goes into the token and
            // what the employees screen shows. Loading roles alone would issue a token that
            // disagrees with both.
            .Include(user => user.PermissionOverrides.Where(entry => !entry.IsDeleted))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<User?> FindWithRolesAsync(long userId, CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(user => !user.IsDeleted && user.Id == userId)
            .Include(user => user.Tenant)
            .Include(user => user.UserRoles.Where(userRole => !userRole.IsDeleted))
                .ThenInclude(userRole => userRole.Role)
            // Overrides come along, because the effective grant is what goes into the token and
            // what the employees screen shows. Loading roles alone would issue a token that
            // disagrees with both.
            .Include(user => user.PermissionOverrides.Where(entry => !entry.IsDeleted))
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsEmailTakenAsync(
        string normalizedEmail,
        long? excludingUserId = null,
        CancellationToken cancellationToken = default) =>
        Set
            // Addresses are unique platform-wide, so uniqueness has to be checked platform-wide.
            // Returning only a boolean means no cross-tenant data is disclosed.
            .IgnoreQueryFilters()
            .AnyAsync(
                user => !user.IsDeleted
                        && user.NormalizedEmail == normalizedEmail
                        && (excludingUserId == null || user.Id != excludingUserId),
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(
        long userId,
        CancellationToken cancellationToken = default) =>
        await Context.UserRoles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(userRole => !userRole.IsDeleted && userRole.UserId == userId)
            .Select(userRole => userRole.Role.Name)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> GetPlatformAdministratorsAsync(
        CancellationToken cancellationToken = default) =>
        await Set
            .IgnoreQueryFilters()
            .Where(user => !user.IsDeleted
                           && user.Status == UserStatus.Active
                           && user.UserRoles.Any(userRole =>
                               !userRole.IsDeleted && userRole.Role.Name == Roles.SuperAdmin))
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> GetTenantAdministratorsAsync(
        long tenantId,
        CancellationToken cancellationToken = default) =>
        await Set
            // No principal on a scheduled job, so the ambient tenant is null and the filter would
            // exclude every candidate. The tenant is pinned explicitly on the next line instead.
            .IgnoreQueryFilters()
            .Where(user => !user.IsDeleted
                           && user.TenantId == tenantId
                           && user.Status == UserStatus.Active
                           && user.UserRoles.Any(userRole =>
                               !userRole.IsDeleted
                               && userRole.Role.Name == Roles.Admin))
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);
}
