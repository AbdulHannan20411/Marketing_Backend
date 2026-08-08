using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Extensions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IEmployeeService" />
public sealed class EmployeeService : IEmployeeService
{
    private readonly IUserRepository _users;
    private readonly IRepository<UserPermissionOverride> _overrides;
    private readonly IRepository<PermissionSet> _permissionSets;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly IAccountActivationService _activation;

    /// <summary>Initialises a new instance.</summary>
    public EmployeeService(
        IUserRepository users,
        IRepository<UserPermissionOverride> overrides,
        IRepository<PermissionSet> permissionSets,
        IRepository<TenantSubscription> subscriptions,
        IRepository<SubscriptionPlan> plans,
        IRefreshTokenRepository refreshTokens,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        IAccountActivationService activation)
    {
        _users = users;
        _overrides = overrides;
        _permissionSets = permissionSets;
        _subscriptions = subscriptions;
        _plans = plans;
        _refreshTokens = refreshTokens;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tenantContext = tenantContext;
        _clock = clock;
        _activation = activation;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmployeeResponse>> GetEmployeesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(
            _users.Query()
                .OrderBy(user => user.DisplayName)
                .Select(user => new EmployeeRow(
                    user.Id,
                    user.DisplayName,
                    user.Email,
                    user.JobTitle,
                    user.Status,
                    user.UserRoles.Where(userRole => !userRole.IsDeleted)
                        .Select(userRole => userRole.Role.Name).ToList(),
                    user.PermissionOverrides.Where(entry => !entry.IsDeleted)
                        .Select(entry => new OverrideRow(entry.Permission, entry.IsGranted)).ToList(),
                    user.LastLoginOn,
                    user.CreatedOn)),
            cancellationToken);

        return [.. rows.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionSetResponse>> GetPermissionSetsAsync(
        CancellationToken cancellationToken = default)
    {
        var sets = await _queries.ToListAsync(
            _permissionSets.Query().OrderByDescending(set => set.IsSystem).ThenBy(set => set.Name),
            cancellationToken);

        var employees = await GetEmployeesAsync(cancellationToken);

        return [.. sets.Select(set => new PermissionSetResponse(
            PublicId.From(PublicId.PermissionSet, set.Id),
            set.Name,
            set.Description,
            set.IsSystem,
            set.Permissions,
            // "Assigned" means holding every permission in the set. There is no stored link
            // between an employee and a set - a set is a template applied at a point in time -
            // so the count is derived from what people actually hold.
            employees.Count(employee => set.Permissions.All(
                permission => employee.Permissions.Contains(permission, StringComparer.Ordinal)))))];
    }

    /// <inheritdoc />
    public async Task<EmployeeResponse> InviteAsync(
        InviteEmployeeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = _tenantContext.RequireTenantId();
        var normalizedEmail = request.Email.ToNormalisedEmail();

        if (await _users.IsEmailTakenAsync(normalizedEmail, cancellationToken: cancellationToken))
        {
            throw new BusinessRuleException("email_taken", "That email address is already registered.");
        }

        await EnsureSeatAvailableAsync(cancellationToken);

        var employee = new User
        {
            TenantId = tenantId,
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            DisplayName = request.Name.Trim(),
            JobTitle = request.JobTitle.Trim(),

            // An unusable placeholder, not a guessable default. The invitation flow sets a real
            // password; until then this hash matches nothing anyone can type.
            PasswordHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N")),

            Status = AppConstants.UserStatus.Invited,
            SecurityStamp = Guid.NewGuid(),
        };

        _users.Add(employee);

        if (request.Permissions is { Count: > 0 })
        {
            foreach (var (permission, isGranted) in EffectivePermissions.Diff([Roles.Employee], request.Permissions))
            {
                _overrides.Add(new UserPermissionOverride
                {
                    TenantId = tenantId,

                    // By navigation: the employee is created in this same unit of work, so their
                    // key does not exist until the insert.
                    User = employee,
                    Permission = permission,
                    IsGranted = isGranted,
                });
            }
        }

        // Issued before the commit so the invitation token and the account are written in one
        // transaction. An account with no way to activate it would be worse than no account.
        await _activation.SendInvitationAsync(employee, null, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(employee.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmployeeResponse> UpdatePermissionsAsync(
        string employeeId,
        UpdatePermissionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.FindWithRolesAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        var roleNames = employee.UserRoles.Select(userRole => userRole.Role.Name).ToList();

        var existing = await _queries.ToListAsync(
            _overrides.Query(asNoTracking: false).Where(entry => entry.UserId == id),
            cancellationToken);

        // Replaced wholesale rather than merged: the caller sent the complete effective set they
        // want, so any override not implied by it is by definition no longer wanted.
        foreach (var entry in existing)
        {
            _overrides.Remove(entry);
        }

        foreach (var (permission, isGranted) in EffectivePermissions.Diff(roleNames, request.Permissions))
        {
            _overrides.Add(new UserPermissionOverride
            {
                TenantId = employee.TenantId,
                UserId = employee.Id,
                Permission = permission,
                IsGranted = isGranted,
            });
        }

        // Rotating the stamp is what makes the change take effect promptly. Without it the
        // employee keeps their old permissions until their access token lapses, which for a
        // revocation is exactly the wrong behaviour.
        var tracked = await _users.GetForUpdateAsync(id, cancellationToken);

        if (tracked is not null)
        {
            tracked.SecurityStamp = Guid.NewGuid();
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmployeeResponse> UpdateStatusAsync(
        string employeeId,
        UpdateEmployeeStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        employee.Status = request.Status switch
        {
            EmployeeStatus.Active => AppConstants.UserStatus.Active,
            EmployeeStatus.Invited => AppConstants.UserStatus.Invited,
            EmployeeStatus.Suspended => AppConstants.UserStatus.Disabled,
            _ => employee.Status,
        };

        if (request.Status != EmployeeStatus.Active)
        {
            // Suspending someone has to end their live sessions, or they keep working until their
            // token expires - which is not what "suspended" means to whoever pressed the button.
            employee.SecurityStamp = Guid.NewGuid();

            await _refreshTokens.RevokeAllForUserAsync(
                employee.Id,
                $"Account set to {request.Status}.",
                _clock.UtcNow,
                cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        await _refreshTokens.RevokeAllForUserAsync(
            employee.Id,
            "Account removed.",
            _clock.UtcNow,
            cancellationToken);

        // Soft delete, applied by the interceptor. The audit trail keeps referring to this person
        // by name, so the row has to survive even though the account is gone.
        _users.Remove(employee);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PermissionSetResponse> CreatePermissionSetAsync(
        PermissionSetDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var set = new PermissionSet
        {
            TenantId = _tenantContext.RequireTenantId(),
            Name = draft.Name.Trim(),
            Description = draft.Description,
            IsSystem = false,
            Permissions = [.. draft.Permissions.Where(Permissions.IsKnown)],
        };

        _permissionSets.Add(set);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new PermissionSetResponse(
            PublicId.From(PublicId.PermissionSet, set.Id),
            set.Name,
            set.Description,
            set.IsSystem,
            set.Permissions,
            0);
    }

    /// <inheritdoc />
    public async Task<PermissionSetResponse> UpdatePermissionSetAsync(
        string permissionSetId,
        PermissionSetDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var set = await LoadSetForUpdateAsync(permissionSetId, cancellationToken);

        set.Name = draft.Name.Trim();
        set.Description = draft.Description;
        set.Permissions = [.. draft.Permissions.Where(Permissions.IsKnown)];

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var sets = await GetPermissionSetsAsync(cancellationToken);

        return sets.First(existing => existing.Id == PublicId.From(PublicId.PermissionSet, set.Id));
    }

    /// <inheritdoc />
    public async Task DeletePermissionSetAsync(string permissionSetId, CancellationToken cancellationToken = default)
    {
        var set = await LoadSetForUpdateAsync(permissionSetId, cancellationToken);

        if (set.IsSystem)
        {
            throw new BusinessRuleException(
                "system_permission_set",
                $"\"{set.Name}\" ships with the platform and cannot be deleted.");
        }

        _permissionSets.Remove(set);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Refuses an invitation once the plan's seat allowance is used.</summary>
    private async Task EnsureSeatAvailableAsync(CancellationToken cancellationToken)
    {
        var subscription = await _queries.FirstOrDefaultAsync(_subscriptions.Query(), cancellationToken);

        if (subscription is null)
        {
            return;
        }

        var plan = await _queries.FirstOrDefaultAsync(
            _plans.Query().Where(plan => plan.Id == subscription.SubscriptionPlanId),
            cancellationToken);

        // Null means unlimited, so there is nothing to enforce.
        if (plan?.MaxEmployees is not { } maxEmployees)
        {
            return;
        }

        var current = await _queries.CountAsync(_users.Query(), cancellationToken);

        if (current < maxEmployees)
        {
            return;
        }

        // 422 with the limit named, so the message tells the customer what to do rather than just
        // that something went wrong.
        throw new ValidationException(
            "permissions",
            $"The {plan.Name} plan includes {maxEmployees} seats and all of them are in use. "
            + "Upgrade the plan or remove an employee first.");
    }

    private async Task<PermissionSet> LoadSetForUpdateAsync(string permissionSetId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.PermissionSet, permissionSetId, "permission set");

        return await _permissionSets.GetForUpdateAsync(id, cancellationToken)
               ?? throw new NotFoundException("Permission set", permissionSetId);
    }

    private async Task<EmployeeResponse> LoadOneAsync(long id, CancellationToken cancellationToken)
    {
        var employees = await GetEmployeesAsync(cancellationToken);
        var publicId = PublicId.From(PublicId.Employee, id);

        return employees.FirstOrDefault(employee => employee.Id == publicId)
               ?? throw new NotFoundException("Employee", publicId);
    }

    private static EmployeeResponse Map(EmployeeRow row)
    {
        var overrides = row.Overrides
            .Select(entry => new UserPermissionOverride { Permission = entry.Permission, IsGranted = entry.IsGranted })
            .ToList();

        return new EmployeeResponse(
            PublicId.From(PublicId.Employee, row.Id),
            row.DisplayName,
            Initials.From(row.DisplayName),
            row.Email,
            row.JobTitle,
            Roles.Primary(row.RoleNames),
            row.Status switch
            {
                AppConstants.UserStatus.Active => EmployeeStatus.Active,
                AppConstants.UserStatus.Invited => EmployeeStatus.Invited,
                _ => EmployeeStatus.Suspended,
            },
            EffectivePermissions.Resolve(row.RoleNames, overrides),
            row.LastLoginOn,
            row.CreatedOn);
    }

    private sealed record OverrideRow(string Permission, bool IsGranted);

    private sealed record EmployeeRow(
        long Id,
        string DisplayName,
        string Email,
        string JobTitle,
        AppConstants.UserStatus Status,
        List<string> RoleNames,
        List<OverrideRow> Overrides,
        DateTimeOffset? LastLoginOn,
        DateTimeOffset CreatedOn);
}
