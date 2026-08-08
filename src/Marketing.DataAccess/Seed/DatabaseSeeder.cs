using Marketing.Common.Constants;
using Marketing.Common.Extensions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.DataAccess.Seed;

/// <summary>
/// Brings a fresh database to a usable baseline: the three system roles and one bootstrap
/// super administrator.
/// <para>
/// Idempotent by design - safe to run on every startup, which is what makes it usable both on a
/// developer's first clone and as a deployment step.
/// </para>
/// </summary>
public sealed partial class DatabaseSeeder
{
    private readonly ApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<DatabaseSeeder> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    /// <param name="passwordHasher">Hasher used for the bootstrap administrator's password.</param>
    /// <param name="logger">Logger.</param>
    public DatabaseSeeder(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    /// <summary>Seeds roles and the bootstrap super administrator if they are absent.</summary>
    /// <param name="bootstrapAdminEmail">Address of the first super administrator.</param>
    /// <param name="bootstrapAdminPassword">
    /// Initial password, supplied from configuration - user secrets locally, the secret store
    /// elsewhere. Never defaulted in code.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SeedAsync(
        string bootstrapAdminEmail,
        string bootstrapAdminPassword,
        CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync(cancellationToken);
        await SeedSuperAdministratorAsync(bootstrapAdminEmail, bootstrapAdminPassword, cancellationToken);
    }

    /// <summary>
    /// Creates any missing system role, and reconciles the permission set of existing ones.
    /// <para>
    /// Reconciling matters: when a release adds a permission to <see cref="Permissions.ForRole"/>,
    /// existing deployments have to pick it up. Without this, only brand-new databases would ever
    /// receive the new grant.
    /// </para>
    /// </summary>
    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Roles.SuperAdmin] = "Operates the platform and may act across every tenant.",
            [Roles.Admin] = "Administers a single tenant: billing, users, WhatsApp connection and all tenant data.",
            [Roles.Employee] = "Operates campaigns, contacts and templates within a tenant.",
        };

        var existing = await _context.Roles
            .IgnoreQueryFilters()
            .Where(role => !role.IsDeleted)
            .ToDictionaryAsync(role => role.NormalizedName, StringComparer.Ordinal, cancellationToken);

        var created = 0;
        var reconciled = 0;

        foreach (var name in Roles.All)
        {
            var normalized = Roles.Normalise(name);
            var permissions = Permissions.ForRole(name);

            if (existing.TryGetValue(normalized, out var role))
            {
                if (role.Permissions.SequenceEqual(permissions, StringComparer.Ordinal))
                {
                    continue;
                }

                role.Permissions = [.. permissions];
                reconciled++;
                continue;
            }

            _context.Roles.Add(new Role
            {
                Name = name,
                NormalizedName = normalized,
                Description = descriptions[name],
                IsSystemRole = true,
                Permissions = [.. permissions],
            });

            created++;
        }

        if (created == 0 && reconciled == 0)
        {
            return;
        }

        await _context.SaveChangesAsync(cancellationToken);

        LogRolesSeeded(created, reconciled);
    }

    private async Task SeedSuperAdministratorAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            LogBootstrapCredentialsMissing();
            return;
        }

        var normalizedEmail = email.ToNormalisedEmail();

        var alreadyExists = await _context.Users
            .IgnoreQueryFilters()
            .AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

        if (alreadyExists)
        {
            return;
        }

        var superAdminName = Roles.Normalise(Roles.SuperAdmin);

        var superAdminRole = await _context.Roles
            .IgnoreQueryFilters()
            .SingleAsync(role => role.NormalizedName == superAdminName, cancellationToken);

        var user = new User
        {
            TenantId = null, // Platform staff are deliberately outside every tenant.
            Email = email.Trim(),
            NormalizedEmail = normalizedEmail,
            DisplayName = "Super Administrator",
            PasswordHash = _passwordHasher.Hash(password),
            Status = UserStatus.Active,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid(),
        };

        _context.Users.Add(user);

        // The user's key is assigned by the database, so it is still zero here. Relating the two
        // through the navigation property lets Entity Framework order the inserts and fill the
        // foreign key in afterwards; assigning UserId directly would persist a zero.
        _context.UserRoles.Add(new UserRole
        {
            User = user,
            RoleId = superAdminRole.Id,
        });

        await _context.SaveChangesAsync(cancellationToken);

        // Warning rather than information: a live credential exists with a known password until
        // somebody rotates it, and that should not scroll past unnoticed in a startup log.
        LogSuperAdministratorSeeded(normalizedEmail);
    }

    // Source-generated logging. The generator emits a strongly typed, allocation-free call that
    // checks IsEnabled before touching its arguments, which is what CA1873 asks for - the
    // hand-written overloads box every value type into an object[] whether or not the level is on.
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Seeded system roles. Created: {CreatedCount}. Permission sets reconciled: {ReconciledCount}.")]
    private partial void LogRolesSeeded(int createdCount, int reconciledCount);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Bootstrap administrator credentials were not configured; skipping administrator seeding.")]
    private partial void LogBootstrapCredentialsMissing();

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Seeded bootstrap super administrator {Email}. Rotate this password immediately.")]
    private partial void LogSuperAdministratorSeeded(string email);
}
