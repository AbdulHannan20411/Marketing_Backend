using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketing.DataAccess.Configurations;

/// <summary>Fluent configuration for <see cref="AuditLog"/>.</summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(log => log.Id);
        builder.Property(log => log.Id).ValueGeneratedNever();

        builder.Property(log => log.EntityName).IsRequired().HasMaxLength(128);
        builder.Property(log => log.EntityId).IsRequired().HasMaxLength(64);

        builder.Property(log => log.Action)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        // jsonb rather than text: the change payload is queried during investigations with
        // operators such as changes -> 'status' ->> 'new', which text cannot support.
        builder.Property(log => log.Changes).HasColumnType("jsonb");

        builder.Property(log => log.CorrelationId).HasMaxLength(64);
        builder.Property(log => log.IpAddress).HasMaxLength(45);
        builder.Property(log => log.OccurredOn).IsRequired();

        // "What happened in this tenant recently" is the query the audit screen runs; descending
        // time as the second key lets it be answered by an index-only backwards scan.
        builder.HasIndex(log => new { log.TenantId, log.OccurredOn })
            .IsDescending(false, true);

        // "Everything that ever happened to this row" - the entity detail drawer.
        builder.HasIndex(log => new { log.EntityName, log.EntityId });

        builder.HasIndex(log => log.UserId);
    }
}
