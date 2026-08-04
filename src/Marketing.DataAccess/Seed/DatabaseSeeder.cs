using Marketing.Common.Constants;
using Marketing.Common.Enums;
using Marketing.Common.Extensions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Marketing.DataAccess.Seed;

/// <summary>
/// Brings a fresh database to a usable baseline: the three system roles and one bootstrap platform
/// administrator.
/// <para>
/// Idempotent by design - it is safe to run on every startup, which is what makes it usable both
/// on a developer's first clone and as a deployment step.
/// </para>
/// </summary>
public sealed class DatabaseSeeder
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

    /// <summary>Seeds roles and the bootstrap administrator if they are absent.</summary>
    /// <param name="bootstrapAdminEmail">Address of the first platform administrator.</param>
    /// <param name="bootstrapAdminPassword">
    /// Initial password. Supplied from configuration - user secrets locally, the secret store in
    /// every other environment. Never defaulted in code.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SeedAsync(
        string bootstrapAdminEmail,
        string bootstrapAdminPassword,
        CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync(cancellationToken);
        await SeedPlatformAdministratorAsync(bootstrapAdminEmail, bootstrapAdminPassword, cancellationToken);
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        var definitions = new (string Name, string Description, string[] Permissions)[]
        {
            (RoleNames.PlatformAdmin,
                "Operates the platform and may act across every tenant.",
                ["platform:*"]),
            (RoleNames.TenantOwner,
                "Owns a tenant: billing, users, WhatsApp connection and all tenant data.",
                ["tenant:*"]),
            (RoleNames.TenantUser,
                "Operates campaigns, contacts and templates within a tenant.",
                [
                    "contacts:read", "contacts:write",
                    "groups:read", "groups:write",
                    "tags:read", "tags:write",
                    "templates:read",
                    "campaigns:read", "campaigns:write",
                    "reports:read",
                ]),
        };

        var existing = await _context.Roles
            .IgnoreQueryFilters()
            .Select(role => role.NormalizedName)
            .ToListAsync(cancellationToken);

        var existingNames = new HashSet<string>(existing, StringComparer.Ordinal);
        var added = 0;

        foreach (var (name, description, permissions) in definitions)
        {
            var normalized = name.ToUpperInvariant();

            if (existingNames.Contains(normalized))
            {
                continue;
            }

            _context.Roles.Add(new Role
            {
                Id = SequentialGuid.Create(),
                Name = name,
                NormalizedName = normalized,
                Description = description,
                IsSystemRole = true,
                Permissions = [.. permissions],
            });

            added++;
        }

        if (added == 0)
        {
            return;
        }

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Seeded {RoleCount} system roles.", added);
    }

    private async Task SeedPlatformAdministratorAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "Bootstrap administrator credentials were not configured; skipping administrator seeding.");
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

        var adminRole = await _context.Roles
            .IgnoreQueryFilters()
            .SingleAsync(role => role.NormalizedName == RoleNames.PlatformAdmin.ToUpperInvariant(), cancellationToken);

        var user = new User
        {
            Id = SequentialGuid.Create(),
            TenantId = null, // Platform administrators are deliberately outside every tenant.
            Email = email.Trim(),
            NormalizedEmail = normalizedEmail,
            DisplayName = "Platform Administrator",
            PasswordHash = _passwordHasher.Hash(password),
            Status = UserStatus.Active,
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid(),
        };

        _context.Users.Add(user);
        _context.UserRoles.Add(new UserRole
        {
            Id = SequentialGuid.Create(),
            UserId = user.Id,
            RoleId = adminRole.Id,
        });

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Seeded bootstrap platform administrator {Email}. Rotate this password immediately.",
            normalizedEmail);
    }
}
