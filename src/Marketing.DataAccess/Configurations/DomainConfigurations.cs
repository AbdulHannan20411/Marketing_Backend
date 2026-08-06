using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketing.DataAccess.Configurations;

/// <summary>Fluent configuration for <see cref="Contact"/>.</summary>
public sealed class ContactConfiguration : BaseEntityConfiguration<Contact>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts");

        builder.Property(contact => contact.FullName).IsRequired().HasMaxLength(200);
        builder.Property(contact => contact.PhoneNumber).IsRequired().HasMaxLength(32);
        builder.Property(contact => contact.NormalizedPhoneNumber).IsRequired().HasMaxLength(32);
        builder.Property(contact => contact.Email).HasMaxLength(320);
        builder.Property(contact => contact.Country).HasMaxLength(2);
        builder.Property(contact => contact.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(contact => contact.Lifecycle).IsRequired().HasMaxLength(16).HasConversion<string>();

        // One number, one contact, per tenant. Two rows for the same number would duplicate every
        // campaign send and make delivery receipts ambiguous.
        builder.HasIndex(contact => new { contact.TenantId, contact.NormalizedPhoneNumber })
            .IsUnique()
            .HasFilter("is_deleted = false");

        // The contacts list filters by status and sorts by creation date within a tenant.
        builder.HasIndex(contact => new { contact.TenantId, contact.Status, contact.CreatedOn });

        // Supports the lead and customer counters on the platform overview.
        builder.HasIndex(contact => new { contact.TenantId, contact.Lifecycle });
    }
}

/// <summary>Fluent configuration for <see cref="ContactGroup"/>.</summary>
public sealed class ContactGroupConfiguration : BaseEntityConfiguration<ContactGroup>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactGroup> builder)
    {
        builder.ToTable("contact_groups");

        builder.Property(group => group.Name).IsRequired().HasMaxLength(120);
        builder.Property(group => group.Description).HasMaxLength(500);

        builder.HasIndex(group => new { group.TenantId, group.Name })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="ContactGroupMember"/>.</summary>
public sealed class ContactGroupMemberConfiguration : BaseEntityConfiguration<ContactGroupMember>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactGroupMember> builder)
    {
        builder.ToTable("contact_group_members");

        builder.HasIndex(member => new { member.ContactGroupId, member.ContactId })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne(member => member.Contact)
            .WithMany(contact => contact.GroupMemberships)
            .HasForeignKey(member => member.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(member => member.ContactGroup)
            .WithMany(group => group.Members)
            .HasForeignKey(member => member.ContactGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fluent configuration for <see cref="ContactTag"/>.</summary>
public sealed class ContactTagConfiguration : BaseEntityConfiguration<ContactTag>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactTag> builder)
    {
        builder.ToTable("contact_tags");

        builder.Property(tag => tag.Name).IsRequired().HasMaxLength(60);
        builder.Property(tag => tag.Color).IsRequired().HasMaxLength(16).HasConversion<string>();

        builder.HasIndex(tag => new { tag.TenantId, tag.Name })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="ContactTagAssignment"/>.</summary>
public sealed class ContactTagAssignmentConfiguration : BaseEntityConfiguration<ContactTagAssignment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactTagAssignment> builder)
    {
        builder.ToTable("contact_tag_assignments");

        builder.HasIndex(assignment => new { assignment.ContactTagId, assignment.ContactId })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne(assignment => assignment.Contact)
            .WithMany(contact => contact.TagAssignments)
            .HasForeignKey(assignment => assignment.ContactId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(assignment => assignment.ContactTag)
            .WithMany(tag => tag.Assignments)
            .HasForeignKey(assignment => assignment.ContactTagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fluent configuration for <see cref="WhatsAppConnection"/>.</summary>
public sealed class WhatsAppConnectionConfiguration : BaseEntityConfiguration<WhatsAppConnection>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WhatsAppConnection> builder)
    {
        builder.ToTable("whatsapp_connections");

        builder.Property(connection => connection.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(connection => connection.QualityRating).IsRequired().HasMaxLength(8).HasConversion<string>();
        builder.Property(connection => connection.WabaId).HasMaxLength(64);
        builder.Property(connection => connection.PhoneNumberId).HasMaxLength(64);
        builder.Property(connection => connection.DisplayPhoneNumber).HasMaxLength(32);
        builder.Property(connection => connection.VerifiedName).HasMaxLength(200);
        builder.Property(connection => connection.BusinessProfileAbout).HasMaxLength(512);
        builder.Property(connection => connection.BusinessCategory).HasMaxLength(120);
        builder.Property(connection => connection.TemplateNamespaceAlias).HasMaxLength(120);
        builder.Property(connection => connection.EncryptedAccessToken).HasMaxLength(2048);

        builder.HasIndex(connection => connection.TenantId)
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="MessageTemplate"/>.</summary>
public sealed class MessageTemplateConfiguration : BaseEntityConfiguration<MessageTemplate>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MessageTemplate> builder)
    {
        builder.ToTable("message_templates");

        builder.Property(template => template.Name).IsRequired().HasMaxLength(120);
        builder.Property(template => template.MetaTemplateId).HasMaxLength(64);
        builder.Property(template => template.Language).IsRequired().HasMaxLength(16);
        builder.Property(template => template.Category).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(template => template.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(template => template.QualityScore).IsRequired().HasMaxLength(8).HasConversion<string>();
        builder.Property(template => template.HeaderText).HasMaxLength(120);
        builder.Property(template => template.BodyText).IsRequired().HasMaxLength(2048);
        builder.Property(template => template.FooterText).HasMaxLength(120);
        builder.Property(template => template.RejectionReason).HasMaxLength(500);
        builder.Property(template => template.Variables).HasColumnType("text[]").IsRequired();
        builder.Property(template => template.Buttons).HasColumnType("text[]").IsRequired();

        // Meta allows one template per name and language.
        builder.HasIndex(template => new { template.TenantId, template.Name, template.Language })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="Campaign"/>.</summary>
public sealed class CampaignConfiguration : BaseEntityConfiguration<Campaign>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Campaign> builder)
    {
        builder.ToTable("campaigns");

        builder.Property(campaign => campaign.Name).IsRequired().HasMaxLength(200);
        builder.Property(campaign => campaign.TemplateName).IsRequired().HasMaxLength(120);
        builder.Property(campaign => campaign.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(campaign => campaign.AudienceLabel).HasMaxLength(200);
        builder.Property(campaign => campaign.CreatedByName).HasMaxLength(150);

        builder.HasIndex(campaign => new { campaign.TenantId, campaign.Status, campaign.CreatedOn });

        builder.HasOne(campaign => campaign.MessageTemplate)
            .WithMany()
            .HasForeignKey(campaign => campaign.MessageTemplateId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Fluent configuration for <see cref="DeliveryFailure"/>.</summary>
public sealed class DeliveryFailureConfiguration : BaseEntityConfiguration<DeliveryFailure>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<DeliveryFailure> builder)
    {
        builder.ToTable("delivery_failures");

        builder.Property(failure => failure.CampaignName).HasMaxLength(200);
        builder.Property(failure => failure.ContactName).HasMaxLength(200);
        builder.Property(failure => failure.PhoneNumber).HasMaxLength(32);
        builder.Property(failure => failure.Reason).HasMaxLength(500);

        // The failures report is newest-first within a tenant, so the index carries the sort.
        builder.HasIndex(failure => new { failure.TenantId, failure.OccurredOn })
            .IsDescending(false, true);

        builder.HasOne(failure => failure.Campaign)
            .WithMany()
            .HasForeignKey(failure => failure.CampaignId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Fluent configuration for <see cref="MessageDailyStat"/>.</summary>
public sealed class MessageDailyStatConfiguration : BaseEntityConfiguration<MessageDailyStat>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MessageDailyStat> builder)
    {
        builder.ToTable("message_daily_stats");

        builder.Property(stat => stat.Date).IsRequired().HasColumnType("date");

        // One row per tenant per day, and the dashboard reads a contiguous range of them.
        builder.HasIndex(stat => new { stat.TenantId, stat.Date })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="ActivityEntry"/>.</summary>
public sealed class ActivityEntryConfiguration : BaseEntityConfiguration<ActivityEntry>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ActivityEntry> builder)
    {
        builder.ToTable("activity_entries");

        builder.Property(entry => entry.Actor).HasMaxLength(150);
        builder.Property(entry => entry.Action).HasMaxLength(120);
        builder.Property(entry => entry.Subject).HasMaxLength(200);

        builder.HasIndex(entry => new { entry.TenantId, entry.OccurredOn })
            .IsDescending(false, true);
    }
}

/// <summary>Fluent configuration for <see cref="SubscriptionPlan"/>.</summary>
public sealed class SubscriptionPlanConfiguration : BaseEntityConfiguration<SubscriptionPlan>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        builder.ToTable("subscription_plans");

        // Plans are platform-level: defined once, bought by every tenant.
        builder.Ignore(plan => plan.TenantId);

        builder.Property(plan => plan.Name).IsRequired().HasMaxLength(120);
        builder.Property(plan => plan.Tagline).HasMaxLength(200);
        builder.Property(plan => plan.Currency).IsRequired().HasMaxLength(3);
        builder.Property(plan => plan.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(plan => plan.SupportLevel).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(plan => plan.EnabledModules).HasColumnType("text[]").IsRequired();
        builder.Property(plan => plan.Highlights).HasColumnType("text[]").IsRequired();

        builder.HasIndex(plan => new { plan.Status, plan.SortOrder });
    }
}

/// <summary>Fluent configuration for <see cref="TenantSubscription"/>.</summary>
public sealed class TenantSubscriptionConfiguration : BaseEntityConfiguration<TenantSubscription>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TenantSubscription> builder)
    {
        builder.ToTable("tenant_subscriptions");

        builder.Property(subscription => subscription.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(subscription => subscription.BillingCycle).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(subscription => subscription.Currency).IsRequired().HasMaxLength(3);

        // One live subscription per tenant.
        builder.HasIndex(subscription => subscription.TenantId)
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne(subscription => subscription.SubscriptionPlan)
            .WithMany()
            .HasForeignKey(subscription => subscription.SubscriptionPlanId)
            // Restrict, not cascade: deleting a plan must never delete the subscriptions that
            // reference it, because existing subscribers keep their terms.
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Fluent configuration for <see cref="Invoice"/>.</summary>
public sealed class InvoiceConfiguration : BaseEntityConfiguration<Invoice>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.Property(invoice => invoice.Number).IsRequired().HasMaxLength(40);
        builder.Property(invoice => invoice.PlanName).HasMaxLength(120);
        builder.Property(invoice => invoice.Currency).IsRequired().HasMaxLength(3);
        builder.Property(invoice => invoice.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(invoice => invoice.BillingCycle).IsRequired().HasMaxLength(16).HasConversion<string>();

        builder.HasIndex(invoice => invoice.Number).IsUnique();
        builder.HasIndex(invoice => new { invoice.TenantId, invoice.IssuedAt }).IsDescending(false, true);
    }
}

/// <summary>Fluent configuration for <see cref="Payment"/>.</summary>
public sealed class PaymentConfiguration : BaseEntityConfiguration<Payment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");

        builder.Property(payment => payment.InvoiceNumber).HasMaxLength(40);
        builder.Property(payment => payment.Currency).IsRequired().HasMaxLength(3);
        builder.Property(payment => payment.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(payment => payment.Method).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(payment => payment.CardBrand).HasMaxLength(32);

        // Four characters, and the length cap is the point: it makes storing a full card number
        // impossible rather than merely discouraged.
        builder.Property(payment => payment.CardLast4).HasMaxLength(4);

        builder.Property(payment => payment.FailureReason).HasMaxLength(500);

        builder.HasIndex(payment => new { payment.TenantId, payment.ProcessedAt }).IsDescending(false, true);

        builder.HasOne(payment => payment.Invoice)
            .WithMany()
            .HasForeignKey(payment => payment.InvoiceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Fluent configuration for <see cref="RenewalRecord"/>.</summary>
public sealed class RenewalRecordConfiguration : BaseEntityConfiguration<RenewalRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<RenewalRecord> builder)
    {
        builder.ToTable("renewal_records");

        builder.Property(renewal => renewal.PlanName).HasMaxLength(120);
        builder.Property(renewal => renewal.Currency).IsRequired().HasMaxLength(3);
        builder.Property(renewal => renewal.BillingCycle).IsRequired().HasMaxLength(16).HasConversion<string>();

        builder.HasIndex(renewal => new { renewal.TenantId, renewal.RenewedAt }).IsDescending(false, true);
    }
}

/// <summary>Fluent configuration for <see cref="PermissionSet"/>.</summary>
public sealed class PermissionSetConfiguration : BaseEntityConfiguration<PermissionSet>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PermissionSet> builder)
    {
        builder.ToTable("permission_sets");

        builder.Property(set => set.Name).IsRequired().HasMaxLength(120);
        builder.Property(set => set.Description).HasMaxLength(500);
        builder.Property(set => set.Permissions).HasColumnType("text[]").IsRequired();

        builder.HasIndex(set => new { set.TenantId, set.Name })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="UserPermissionOverride"/>.</summary>
public sealed class UserPermissionOverrideConfiguration : BaseEntityConfiguration<UserPermissionOverride>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<UserPermissionOverride> builder)
    {
        builder.ToTable("user_permission_overrides");

        builder.Property(entry => entry.Permission).IsRequired().HasMaxLength(64);

        builder.HasIndex(entry => new { entry.UserId, entry.Permission })
            .IsUnique()
            .HasFilter("is_deleted = false");

        builder.HasOne(entry => entry.User)
            .WithMany(user => user.PermissionOverrides)
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fluent configuration for <see cref="Notification"/>.</summary>
public sealed class NotificationConfiguration : BaseEntityConfiguration<Notification>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.Property(notification => notification.Title).IsRequired().HasMaxLength(200);
        builder.Property(notification => notification.Body).HasMaxLength(1000);
        builder.Property(notification => notification.Kind).IsRequired().HasMaxLength(40).HasConversion<string>();
        builder.Property(notification => notification.Priority).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(notification => notification.Icon).HasMaxLength(40);
        builder.Property(notification => notification.ActionLabel).HasMaxLength(60);
        builder.Property(notification => notification.ActionRoute).HasMaxLength(200);

        // The notification centre reads newest-first for one user plus the tenant-wide rows.
        builder.HasIndex(notification => new { notification.TenantId, notification.UserId, notification.OccurredOn })
            .IsDescending(false, false, true);
    }
}
