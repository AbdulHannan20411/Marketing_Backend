using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketing.DataAccess.Configurations;

/// <summary>Fluent configuration for <see cref="RefreshToken"/>.</summary>
public sealed class RefreshTokenConfiguration : BaseEntityConfiguration<RefreshToken>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.Property(token => token.UserId).IsRequired();
        builder.Property(token => token.SessionId).IsRequired();
        builder.Property(token => token.SecurityStamp).IsRequired();

        // 64 hex characters for a SHA-256 digest.
        builder.Property(token => token.TokenHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(token => token.ReplacedByTokenHash)
            .HasMaxLength(64)
            .IsFixedLength();

        builder.Property(token => token.ExpiresOn).IsRequired();
        builder.Property(token => token.RevokedReason).HasMaxLength(256);
        builder.Property(token => token.CreatedByIp).HasMaxLength(45); // IPv6 with a scope id.
        builder.Property(token => token.UserAgent).HasMaxLength(512);

        // The refresh path looks a token up by hash and nothing else, so this index is the whole
        // query plan.
        builder.HasIndex(token => token.TokenHash).IsUnique();

        builder.HasIndex(token => new { token.UserId, token.SessionId });

        // Supports the Quartz cleanup job, which sweeps expired rows.
        builder.HasIndex(token => token.ExpiresOn);
    }
}
