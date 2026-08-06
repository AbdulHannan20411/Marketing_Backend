using static Marketing.Common.Constants.AppConstants;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketing.DataAccess.Configurations;

/// <summary>Fluent configuration for <see cref="Tenant"/>.</summary>
public sealed class TenantConfiguration : BaseEntityConfiguration<Tenant>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        // A tenant is not owned by a tenant; the inherited column would always be null.
        builder.Ignore(tenant => tenant.TenantId);

        builder.Property(tenant => tenant.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(tenant => tenant.Slug)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(tenant => tenant.ContactEmail)
            .IsRequired()
            .HasMaxLength(320); // RFC 5321 maximum path length.

        // Persisted as text rather than an integer: an operator reading the table directly should
        // see "Suspended", and reordering the enum must never silently reinterpret existing rows.
        builder.Property(tenant => tenant.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasDefaultValue(TenantStatus.Pending);

        builder.Property(tenant => tenant.TimeZoneId)
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue("UTC");

        builder.Property(tenant => tenant.CurrencyCode)
            .IsRequired()
            .HasMaxLength(3)
            .HasDefaultValue("USD");

        builder.Property(tenant => tenant.ContactQuota).IsRequired();
        builder.Property(tenant => tenant.MonthlyMessageQuota).IsRequired();

        // Unique across live rows only, so a cancelled tenant's slug becomes available again.
        builder.HasIndex(tenant => tenant.Slug)
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasIndex(tenant => tenant.Status);

        builder.HasMany(tenant => tenant.Users)
            .WithOne(user => user.Tenant)
            .HasForeignKey(user => user.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
