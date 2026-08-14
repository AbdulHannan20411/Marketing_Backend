using Marketing.Application.DTOs.Platform;
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

/// <summary>Admin account administration. Platform staff only.</summary>
public interface IAdminAccountService
{
    /// <summary>Creates an organisation and its first administrator.</summary>
    public Task<AdminAccount> CreateAsync(CreateAdminAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Updates an Admin account. Omitted fields are left unchanged.</summary>
    public Task<AdminAccount> UpdateAsync(
        string adminId,
        UpdateAdminAccountRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Suspends or reactivates an Admin account and its whole organisation.</summary>
    public Task<AdminAccount> UpdateStatusAsync(
        string adminId,
        UpdateAdminStatusRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an Admin account and suspends its organisation.</summary>
    public Task DeleteAsync(string adminId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdminAccountService" />
public sealed class AdminAccountService : IAdminAccountService
{
    private readonly IUserRepository _users;
    private readonly ITenantRepository _tenants;
    private readonly IRepository<UserRole> _userRoles;
    private readonly IRepository<Role> _roles;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPlatformService _platform;
    private readonly IDateTimeProvider _clock;
    private readonly IAccountActivationService _activation;
    private readonly ICurrentUser _currentUser;

    /// <summary>Initialises a new instance.</summary>
    public AdminAccountService(
        IUserRepository users,
        ITenantRepository tenants,
        IRepository<UserRole> userRoles,
        IRepository<Role> roles,
        IRefreshTokenRepository refreshTokens,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IPlatformService platform,
        IDateTimeProvider clock,
        IAccountActivationService activation,
        ICurrentUser currentUser)
    {
        _users = users;
        _tenants = tenants;
        _userRoles = userRoles;
        _roles = roles;
        _refreshTokens = refreshTokens;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _platform = platform;
        _clock = clock;
        _activation = activation;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public async Task<AdminAccount> CreateAsync(
        CreateAdminAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Wrapped explicitly because creating an admin now takes two saves - the tenant has to be
        // inserted before its key can be written onto the role assignment. Without the transaction
        // a failure between them would leave an organisation nobody can sign in to.
        return await _unitOfWork.ExecuteInTransactionAsync(
            token => CreateCoreAsync(request, token),
            cancellationToken);
    }

    private async Task<AdminAccount> CreateCoreAsync(
        CreateAdminAccountRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.ToNormalisedEmail();

        if (await _users.IsEmailTakenAsync(normalizedEmail, cancellationToken: cancellationToken))
        {
            throw new BusinessRuleException("email_taken", "That email address is already registered.");
        }

        var slug = request.Organisation.ToSlug();

        if (await _tenants.IsSlugTakenAsync(slug, cancellationToken: cancellationToken))
        {
            // Slugs appear in sign-in routing and log context, so a collision is a real conflict
            // rather than something to silently disambiguate with a suffix.
            throw new BusinessRuleException(
                "organisation_exists",
                $"An organisation with the address \"{slug}\" already exists.");
        }

        // Organisation and administrator are created together. An Admin with no tenant can do
        // nothing, and a tenant with no Admin cannot be reached - neither half is useful alone.
        var tenant = new Tenant
        {
            Name = request.Organisation.Trim(),
            Slug = slug,
            ContactEmail = request.Email.Trim(),
            Status = AppConstants.TenantStatus.Pending,
        };

        _tenants.Add(tenant);

        var adminRoleName = Roles.Normalise(Roles.Admin);

        var adminRole = await _queries.FirstOrDefaultAsync(
            _roles.Query().Where(role => role.NormalizedName == adminRoleName),
            cancellationToken)
            ?? throw new NotFoundException("The Admin role has not been seeded.");

        // The tenant is inserted first so its key exists for the rows below. UserRole carries a
        // tenant id with no navigation to relate through, so unlike the other cases in this file
        // there is no way to defer it - and a role assignment landing with a null tenant would be
        // indistinguishable from a platform administrator's.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var admin = new User
        {
            TenantId = tenant.Id,
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            DisplayName = request.Name.Trim(),
            JobTitle = "Administrator",

            // An unusable placeholder, and deliberately random. The owner sets a real password
            // through the invitation link; until then this hash matches nothing anyone can type,
            // and nobody - including whoever created the account - knows a value that would.
            PasswordHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N")),

            Status = AppConstants.UserStatus.Invited,
            SecurityStamp = Guid.NewGuid(),
        };

        _users.Add(admin);

        _userRoles.Add(new UserRole
        {
            TenantId = tenant.Id,

            // By navigation: the admin has not been inserted yet, so their key is still zero.
            User = admin,
            RoleId = adminRole.Id,
        });

        // Same transaction as the account and the organisation, so a created admin always has a
        // usable activation link.
        // Attributed to the platform administrator who created the account, for the same reason a
        // colleague's invitation is: the recipient is deciding whether an unexpected message is real.
        await _activation.SendInvitationAsync(
            admin,
            tenant.Name,
            _currentUser is { DisplayName: { Length: > 0 } name, Email: { Length: > 0 } email }
                ? new EmailAttribution(name, email)
                : null,
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(admin.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AdminAccount> UpdateAsync(
        string adminId,
        UpdateAdminAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (admin, tenant) = await LoadForUpdateAsync(adminId, cancellationToken);

        admin.DisplayName = request.Name?.Trim() ?? admin.DisplayName;

        if (request.Organisation is { } organisation)
        {
            tenant.Name = organisation.Trim();
        }

        tenant.PlanBand = request.Plan ?? tenant.PlanBand;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(admin.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AdminAccount> UpdateStatusAsync(
        string adminId,
        UpdateAdminStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (admin, tenant) = await LoadForUpdateAsync(adminId, cancellationToken);

        tenant.Status = request.Status switch
        {
            TenantAccountStatus.Active => AppConstants.TenantStatus.Active,
            TenantAccountStatus.Trialing => AppConstants.TenantStatus.Pending,
            _ => AppConstants.TenantStatus.Suspended,
        };

        if (request.Status == TenantAccountStatus.Active)
        {
            tenant.ActivatedOn ??= _clock.UtcNow;
            tenant.SuspendedOn = null;
            admin.Status = AppConstants.UserStatus.Active;
        }
        else if (request.Status == TenantAccountStatus.Suspended)
        {
            tenant.SuspendedOn = _clock.UtcNow;
            admin.Status = AppConstants.UserStatus.Disabled;

            // Suspending an organisation has to end the administrator's live sessions, or they
            // keep working until their token lapses - which is not what suspension means.
            admin.SecurityStamp = Guid.NewGuid();

            await _refreshTokens.RevokeAllForUserAsync(
                admin.Id,
                "Organisation suspended.",
                _clock.UtcNow,
                cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(admin.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string adminId, CancellationToken cancellationToken = default)
    {
        var (admin, tenant) = await LoadForUpdateAsync(adminId, cancellationToken);

        await _refreshTokens.RevokeAllForUserAsync(
            admin.Id,
            "Administrator account removed.",
            _clock.UtcNow,
            cancellationToken);

        _users.Remove(admin);

        // The organisation is suspended, not deleted. Removing an administrator must not destroy
        // a customer's contacts, campaigns and billing history - that is a separate, deliberate
        // decision with a retention period attached to it.
        tenant.Status = AppConstants.TenantStatus.Suspended;
        tenant.SuspendedOn = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<(User Admin, Tenant Tenant)> LoadForUpdateAsync(
        string adminId,
        CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.AdminAccount, adminId, "admin account");

        var admin = await _users.GetForUpdateAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Admin account", adminId);

        if (admin.TenantId is not { } tenantId)
        {
            throw new BusinessRuleException(
                "not_an_admin_account",
                "That account is platform staff and has no organisation.");
        }

        var tenant = await _tenants.GetForUpdateAsync(tenantId, cancellationToken)
                     ?? throw new NotFoundException("Tenant", tenantId);

        return (admin, tenant);
    }

    /// <summary>Reads the account back through the same projection the platform list uses.</summary>
    private async Task<AdminAccount> LoadOneAsync(long adminId, CancellationToken cancellationToken)
    {
        var accounts = await _platform.GetAdminAccountsAsync(cancellationToken);
        var publicId = PublicId.From(PublicId.AdminAccount, adminId);

        return accounts.FirstOrDefault(account => account.Id == publicId)
               ?? throw new NotFoundException("Admin account", publicId);
    }
}
