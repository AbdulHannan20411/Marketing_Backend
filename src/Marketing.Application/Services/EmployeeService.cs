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
    private readonly IRepository<Role> _roles;
    private readonly IRepository<UserRole> _userRoles;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IRepository<Tenant> _tenants;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly IAccountActivationService _activation;
    private readonly ICurrentUser _currentUser;
    private readonly IPlanGuard _planGuard;

    /// <summary>Initialises a new instance.</summary>
    public EmployeeService(
        IUserRepository users,
        IRepository<UserPermissionOverride> overrides,
        IRepository<PermissionSet> permissionSets,
        IRepository<Role> roles,
        IRepository<UserRole> userRoles,
        IRepository<TenantSubscription> subscriptions,
        IRepository<SubscriptionPlan> plans,
        IRefreshTokenRepository refreshTokens,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IRepository<Tenant> tenants,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        IAccountActivationService activation,
        ICurrentUser currentUser,
        IPlanGuard planGuard)
    {
        _users = users;
        _overrides = overrides;
        _permissionSets = permissionSets;
        _roles = roles;
        _userRoles = userRoles;
        _subscriptions = subscriptions;
        _plans = plans;
        _refreshTokens = refreshTokens;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tenants = tenants;
        _tenantContext = tenantContext;
        _clock = clock;
        _activation = activation;
        _currentUser = currentUser;
        _planGuard = planGuard;
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

        var roleName = request.Role ?? Roles.Employee;

        EmployeeRules.EnsureRoleGrantable(roleName, _currentUser);

        var role = await _queries.FirstOrDefaultAsync(
            _roles.Query().Where(candidate => candidate.NormalizedName == Roles.Normalise(roleName)),
            cancellationToken)
            ?? throw new NotFoundException("Role", roleName);

        // An explicit list wins over a saved set, because it is the more specific instruction.
        var requested = request.Permissions ?? await ResolveSetPermissionsAsync(
            request.PermissionSetId,
            cancellationToken);

        await EnsureGrantableAsync(requested, cancellationToken);

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

        // The role assignment, without which the invitee signs in with no role and therefore no
        // permissions at all - the request's role was previously validated and then discarded.
        _userRoles.Add(new UserRole
        {
            TenantId = tenantId,
            User = employee,
            RoleId = role.Id,
        });

        // An administrator holds everything by role, so there is nothing to write and any set sent
        // alongside the role is ignored rather than silently reducing them.
        //
        // An employee, by contrast, starts with exactly what was asked for, plus
        // Permissions.Baseline and nothing else. The role's defaults are not a floor - falling back
        // to them would hand a new starter twelve permissions the person inviting them never chose.
        //
        // But "exactly what was asked for" cannot mean literally nothing. An invitee with no set
        // named still needs the landing route, or they sign in and every screen including the one
        // they arrive on reports a permission error - which reads as a broken account rather than
        // as one waiting for access. The baseline is folded in inside Diff, so no revoke is ever
        // written for it here.
        if (!Roles.Normalise(role.Name).Equals(Roles.Normalise(Roles.Admin), StringComparison.Ordinal))
        {
            // Diffed against the role actually granted, not against Employee. Diffing against the
            // wrong role writes revokes for permissions the person never had and misses grants for
            // ones they now do.
            foreach (var (permission, isGranted) in EffectivePermissions.Diff([role.Name], requested))
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
        await _activation.SendInvitationAsync(
            employee, await WorkspaceNameAsync(cancellationToken), ActingAs(), cancellationToken);

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

        // An Admin's or Super Admin's access comes from their role, not from overrides. Silently
        // accepting an edit here would show the operator a saved matrix that changes nothing.
        if (roleNames.Any(name =>
                string.Equals(Roles.Normalise(name), Roles.Admin, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Roles.Normalise(name), Roles.SuperAdmin, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessRuleException(
                "role_derived_permissions",
                "This person's permissions come from their role and cannot be edited individually.");
        }

        await EnsureGrantableAsync(request.Permissions, cancellationToken);

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

        await RotateStampAsync(id, cancellationToken);

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

        if (request.Status != EmployeeStatus.Active)
        {
            EmployeeRules.EnsureNotSelf(id, _currentUser, "suspend");
            EmployeeRules.EnsureAnAdminRemains(
                await CountOtherAdminsAsync(id, cancellationToken),
                "suspend");
        }

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
    public async Task<EmployeeResponse> UpdateRoleAsync(
        string employeeId,
        UpdateEmployeeRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        EmployeeRules.EnsureRoleGrantable(request.Role, _currentUser);
        EmployeeRules.EnsureNotSelf(id, _currentUser, "change the role of");

        var employee = await _users.FindWithRolesAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        var target = Roles.Normalise(request.Role);
        var wasAdmin = employee.UserRoles.Any(assignment =>
            string.Equals(assignment.Role.NormalizedName, Roles.Normalise(Roles.Admin), StringComparison.Ordinal));

        // Demoting the only Admin would leave the workspace unmanageable, which costs a support
        // conversation to undo and is never what the operator meant.
        if (wasAdmin && !string.Equals(target, Roles.Normalise(Roles.Admin), StringComparison.Ordinal))
        {
            EmployeeRules.EnsureAnAdminRemains(await CountOtherAdminsAsync(id, cancellationToken), "demote");
        }

        var role = await _queries.FirstOrDefaultAsync(
            _roles.Query().Where(candidate => candidate.NormalizedName == target),
            cancellationToken)
            ?? throw new NotFoundException("Role", request.Role);

        var existing = await _queries.ToListAsync(
            _userRoles.Query(asNoTracking: false).Where(assignment => assignment.UserId == id),
            cancellationToken);

        foreach (var assignment in existing)
        {
            _userRoles.Remove(assignment);
        }

        _userRoles.Add(new UserRole
        {
            TenantId = employee.TenantId,
            UserId = employee.Id,
            RoleId = role.Id,
        });

        await RotateStampAsync(id, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmployeeResponse> UpdateAsync(
        string employeeId,
        UpdateEmployeeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        if (request.Email is { Length: > 0 } email)
        {
            var normalised = email.ToNormalisedEmail();

            if (normalised != employee.NormalizedEmail
                && await _users.IsEmailTakenAsync(normalised, id, cancellationToken))
            {
                throw new BusinessRuleException("email_taken", "That email address is already registered.");
            }

            employee.Email = email.Trim();
            employee.NormalizedEmail = normalised;
        }

        employee.DisplayName = request.Name?.Trim() is { Length: > 0 } name ? name : employee.DisplayName;
        employee.JobTitle = request.JobTitle?.Trim() ?? employee.JobTitle;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ResendInviteAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        if (employee.Status != AppConstants.UserStatus.Invited)
        {
            throw new BusinessRuleException(
                "not_invited",
                "That person has already accepted their invitation.");
        }

        // Issuing a new token consumes the old one, so a forwarded copy of the first email stops
        // working. Resending must not leave two live ways into the same account.
        await _activation.SendInvitationAsync(
            employee, await WorkspaceNameAsync(cancellationToken), ActingAs(), cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RevokeInviteAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        if (employee.Status != AppConstants.UserStatus.Invited)
        {
            throw new BusinessRuleException(
                "not_invited",
                "That person has already accepted their invitation. Remove them instead.");
        }

        // The account never became real, so it goes with the invitation rather than lingering as
        // an invited row nobody can act on. The seat is freed either way.
        _users.Remove(employee);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmployeeResponse>> ApplyPermissionSetAsync(
        string permissionSetId,
        ApplyPermissionSetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var setId = PublicId.Parse(PublicId.PermissionSet, permissionSetId, "permission set");

        var set = await _queries.FirstOrDefaultAsync(
            _permissionSets.Query().Where(candidate => candidate.Id == setId),
            cancellationToken)
            ?? throw new NotFoundException("Permission set", permissionSetId);

        // Applying a set is a grant like any other, so it is subject to the same guards. A saved
        // set must not become a way to hand out something the caller could not grant directly.
        await EnsureGrantableAsync(set.Permissions, cancellationToken);

        var applied = new List<EmployeeResponse>(request.EmployeeIds.Count);

        foreach (var employeeId in request.EmployeeIds)
        {
            applied.Add(await UpdatePermissionsAsync(
                employeeId,
                new UpdatePermissionsRequest(set.Permissions),
                cancellationToken));
        }

        return applied;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Employee, employeeId, "employee");

        var employee = await _users.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Employee", employeeId);

        EmployeeRules.EnsureNotSelf(id, _currentUser, "remove");
        EmployeeRules.EnsureAnAdminRemains(await CountOtherAdminsAsync(id, cancellationToken), "remove");

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
            Permissions = [.. EnsureKnown(draft.Permissions)],
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
        set.Permissions = [.. EnsureKnown(draft.Permissions)];

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
        // Platform staff are not bound by a seat ceiling. It is a billing construct — what the
        // customer has paid for — not a security one, and refusing support staff the ability to add
        // somebody to a workspace they are fixing helps nobody.
        if (_currentUser.IsSuperAdmin)
        {
            return;
        }

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

        // A commercial ceiling, not a malformed request: 409 with the allowance named, so the
        // client can distinguish "fix your input" from "buy more seats" and say so. It was a 422
        // reported against a "permissions" field the invite request does not even have, which gave
        // the form nowhere sensible to put the message.
        throw new BusinessRuleException(
            "seat_limit_reached",
            $"The {plan.Name} plan includes {maxEmployees} seats and all of them are in use. "
            + "Upgrade the plan or remove an employee first.");
    }

    /// <summary>
    /// Rotates an account's security stamp so an access-affecting change lands immediately.
    /// <para>
    /// Without it the employee keeps their old access until their token lapses, which for a
    /// revocation is exactly the wrong behaviour.
    /// </para>
    /// </summary>
    private async Task RotateStampAsync(long employeeId, CancellationToken cancellationToken)
    {
        var tracked = await _users.GetForUpdateAsync(employeeId, cancellationToken);

        if (tracked is not null)
        {
            tracked.SecurityStamp = Guid.NewGuid();
        }
    }

    /// <summary>Reads the permissions out of a saved set, or none when no set was named.</summary>
    private async Task<IReadOnlyList<string>> ResolveSetPermissionsAsync(
        string? permissionSetId,
        CancellationToken cancellationToken)
    {
        if (permissionSetId is not { Length: > 0 })
        {
            return [];
        }

        var id = PublicId.Parse(PublicId.PermissionSet, permissionSetId, "permission set");

        var set = await _queries.FirstOrDefaultAsync(
            _permissionSets.Query().Where(candidate => candidate.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Permission set", permissionSetId);

        return set.Permissions;
    }

    /// <summary>Runs every grant guard over a requested permission set.</summary>
    private async Task EnsureGrantableAsync(
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        if (permissions.Count == 0)
        {
            return;
        }

        EmployeeRules.EnsureKnown(permissions);
        EmployeeRules.EnsureCoherent(permissions);
        EmployeeRules.EnsureCallerHolds(permissions, _currentUser);
        EmployeeRules.EnsureWithinPlan(permissions, await _planGuard.EnabledModulesAsync(cancellationToken));
    }

    /// <summary>Counts the workspace's other administrators, excluding one account.</summary>
    private Task<int> CountOtherAdminsAsync(long excluding, CancellationToken cancellationToken)
    {
        var adminRole = Roles.Normalise(Roles.Admin);

        return _queries.CountAsync(
            _users.Query()
                .Where(user =>
                    user.Id != excluding
                    && user.Status == AppConstants.UserStatus.Active
                    && user.UserRoles.Any(assignment => assignment.Role.NormalizedName == adminRole)),
            cancellationToken);
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

    /// <summary>
    /// The signed-in person, for attribution on mail this action causes.
    /// </summary>
    /// <remarks>
    /// Read from the principal rather than passed in, so a caller cannot claim to be somebody else.
    /// Null when the actor has no address to reply to, in which case the mail goes out unattributed
    /// rather than attributed to a blank.
    /// </remarks>
    private EmailAttribution? ActingAs() =>
        _currentUser is { DisplayName: { Length: > 0 } name, Email: { Length: > 0 } email }
            ? new EmailAttribution(name, email)
            : null;

    /// <summary>The organisation's display name, for the invitation's subject and body.</summary>
    private async Task<string?> WorkspaceNameAsync(CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId;

        if (tenantId is null)
        {
            return null;
        }

        var tenant = await _queries.FirstOrDefaultAsync(
            _tenants.Query().Where(candidate => candidate.Id == tenantId.Value),
            cancellationToken);

        return tenant?.Name;
    }

    /// <summary>
    /// Returns the permissions, or refuses the whole request if any is not in the catalogue.
    /// </summary>
    /// <remarks>
    /// Rejecting rather than filtering, to match what editing an employee's permissions already
    /// does. Quietly dropping an unrecognised name means an administrator ticks a box, saves, and
    /// finds their selection gone with nothing to explain why — and the two endpoints disagreeing
    /// about the same invalid input is worse than either behaviour on its own.
    /// </remarks>
    private static IReadOnlyList<string> EnsureKnown(IReadOnlyList<string> permissions)
    {
        var unknown = permissions.Where(permission => !Permissions.IsKnown(permission)).ToArray();

        return unknown.Length > 0
            ? throw new ValidationException("permissions", $"Unknown permission: {string.Join(", ", unknown)}.")
            : permissions;
    }
}
