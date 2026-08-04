using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketing.DataAccess.Configurations;

/// <summary>Fluent configuration for <see cref="Role"/>.</summary>
public sealed class RoleConfiguration : BaseEntityConfiguration<Role>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        // Roles are defined by the platform and shared by every tenant.
        builder.Ignore(role => role.TenantId);

        builder.Property(role => role.Name)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(role => role.NormalizedName)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(role => role.Description)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(role => role.IsSystemRole)
            .IsRequired()
            .HasDefaultValue(false);

        // Mapped to a native text[] column. Npgsql handles List<string> directly, so there is no
        // serialisation step and the array stays queryable with PostgreSQL's array operators.
        builder.Property(role => role.Permissions)
            .HasColumnType("text[]")
            .IsRequired();

        builder.HasIndex(role => role.NormalizedName)
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}
