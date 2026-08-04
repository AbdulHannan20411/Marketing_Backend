using Marketing.Common.Enums;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketing.DataAccess.Configurations;

/// <summary>Fluent configuration for <see cref="User"/>.</summary>
public sealed class UserConfiguration : BaseEntityConfiguration<User>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.Property(user => user.Email)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(user => user.NormalizedEmail)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(user => user.DisplayName)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(user => user.PasswordHash)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(user => user.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasDefaultValue(UserStatus.Invited);

        builder.Property(user => user.SecurityStamp).IsRequired();
        builder.Property(user => user.EmailConfirmed).IsRequired().HasDefaultValue(false);
        builder.Property(user => user.FailedLoginAttempts).IsRequired().HasDefaultValue(0);

        // Addresses are globally unique across the platform, not per tenant. One address therefore
        // maps to exactly one account, which keeps the sign-in path a single indexed lookup with no
        // tenant disambiguation step - and removes the class of bug where the same person exists
        // twice with divergent passwords.
        builder.HasIndex(user => user.NormalizedEmail)
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasIndex(user => new { user.TenantId, user.Status });

        builder.HasMany(user => user.UserRoles)
            .WithOne(userRole => userRole.User)
            .HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(user => user.RefreshTokens)
            .WithOne(token => token.User)
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
