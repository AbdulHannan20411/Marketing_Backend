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
        builder.Property(connection => connection.MessagingTier).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(connection => connection.WabaId).HasMaxLength(64);
        builder.Property(connection => connection.PhoneNumberId).HasMaxLength(64);
        builder.Property(connection => connection.DisplayPhoneNumber).HasMaxLength(32);
        builder.Property(connection => connection.VerifiedName).HasMaxLength(200);
        builder.Property(connection => connection.BusinessProfileAbout).HasMaxLength(512);
        builder.Property(connection => connection.BusinessCategory).HasMaxLength(120);
        builder.Property(connection => connection.TemplateNamespaceAlias).HasMaxLength(120);
        builder.Property(connection => connection.EncryptedAccessToken).HasMaxLength(2048);
        builder.Property(connection => connection.RegistrationPin).HasMaxLength(6).IsFixedLength();

        builder.HasIndex(connection => connection.TenantId)
            .IsUnique()
            .HasFilter("is_deleted = false");

        // A number belongs to one tenant at a time. Inbound webhooks carry no tenant and are routed
        // solely by this identifier, so two live rows sharing one number would hand a tenant's
        // conversations to whichever row the planner happened to return. Disconnected rows are
        // excluded because they keep the number as history and no longer answer for it.
        builder.HasIndex(connection => connection.PhoneNumberId)
            .IsUnique()
            .HasFilter("is_deleted = false AND status <> 'Disconnected'");
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

        // The templates screen's status chips and its default ordering. Category is left unindexed:
        // it has three values, so a scan of what the tenant filter already narrowed costs less than
        // the planner would spend consulting an index that selective.
        builder.HasIndex(template => new { template.TenantId, template.Status });
        builder.HasIndex(template => new { template.TenantId, template.ModifiedOn });
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

        // bigint[], not a join table. The audience selection is read and written whole, never
        // queried by element, so a child table would add a join to every read and buy nothing.
        builder.Property(campaign => campaign.AudienceGroupIds)
            .HasColumnType("bigint[]");

        builder.Property(campaign => campaign.Description).HasMaxLength(1000);
        builder.Property(campaign => campaign.TimeZone).HasMaxLength(64);
        builder.Property(campaign => campaign.RecurrenceJson).HasColumnType("jsonb");

        builder.HasIndex(campaign => new { campaign.TenantId, campaign.Status, campaign.CreatedOn });

        // The dispatcher's poll: campaigns due to start or due to resume, across every tenant.
        // Status first because it is the selective column - most rows are drafts or completed.
        builder.HasIndex(campaign => new { campaign.Status, campaign.ScheduledAt });

        // The recurring dispatcher's poll. Filtered so the index holds only rows that can ever
        // match: a campaign with no next occurrence is most of the table and none of the answers.
        builder.HasIndex(campaign => campaign.NextRunAtUtc)
            .HasFilter("next_run_at_utc IS NOT NULL AND is_deleted = false");

        builder.HasOne(campaign => campaign.MessageTemplate)
            .WithMany()
            .HasForeignKey(campaign => campaign.MessageTemplateId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Fluent configuration for <see cref="CampaignRun"/>.</summary>
public sealed class CampaignRunConfiguration : BaseEntityConfiguration<CampaignRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<CampaignRun> builder)
    {
        builder.ToTable("campaign_runs");

        builder.Property(run => run.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(run => run.FailureReason).HasMaxLength(500);

        // The idempotency claim. Two pollers racing the same minute, a redelivered message, or a
        // retry after a timeout all collide here instead of dispatching the occurrence twice.
        builder.HasIndex(run => new { run.CampaignId, run.OccurrenceNumber })
            .IsUnique()
            .HasFilter("is_deleted = false");

        // The run history read: newest first for one campaign.
        builder.HasIndex(run => new { run.CampaignId, run.ScheduledForUtc });

        builder.HasOne(run => run.Campaign)
            .WithMany(campaign => campaign.Runs)
            .HasForeignKey(run => run.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);
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

/// <summary>Fluent configuration for <see cref="ContactImportBatch"/>.</summary>
public sealed class ContactImportBatchConfiguration : BaseEntityConfiguration<ContactImportBatch>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactImportBatch> builder)
    {
        builder.ToTable("contact_import_batches");

        builder.Property(batch => batch.FileName).IsRequired().HasMaxLength(260);
        builder.Property(batch => batch.Columns).HasColumnType("text[]").IsRequired();
        builder.Property(batch => batch.Status).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(batch => batch.UploadedByName).HasMaxLength(200);

        // Supports the cleanup job that discards abandoned uploads.
        builder.HasIndex(batch => new { batch.TenantId, batch.Status, batch.UploadedOn });

        builder.HasMany(batch => batch.Rows)
            .WithOne(row => row.ContactImportBatch)
            .HasForeignKey(row => row.ContactImportBatchId)
            // Cascade here, unlike everywhere else: staged rows are worthless without their batch
            // and are not business data worth preserving.
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(batch => batch.Exports)
            .WithOne(export => export.ContactImportBatch)
            .HasForeignKey(export => export.ContactImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fluent configuration for <see cref="PaymentRequest"/>.</summary>
public sealed class PaymentRequestConfiguration : BaseEntityConfiguration<PaymentRequest>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PaymentRequest> builder)
    {
        builder.ToTable("payment_requests");

        builder.Property(request => request.PlanName).IsRequired().HasMaxLength(120);
        builder.Property(request => request.BillingCycle).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(request => request.Amount).HasPrecision(18, 2);
        builder.Property(request => request.Currency).IsRequired().HasMaxLength(3);
        builder.Property(request => request.Channel).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(request => request.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(request => request.Reference).HasMaxLength(120);
        builder.Property(request => request.Note).HasMaxLength(1000);
        builder.Property(request => request.ProofStorageKey).IsRequired().HasMaxLength(400);
        builder.Property(request => request.ProofFileName).HasMaxLength(260);
        builder.Property(request => request.ProofContentType).HasMaxLength(120);
        builder.Property(request => request.Organisation).HasMaxLength(200);
        builder.Property(request => request.SubmittedByName).HasMaxLength(200);
        builder.Property(request => request.SubmittedByEmail).HasMaxLength(256);
        builder.Property(request => request.ReviewedByName).HasMaxLength(200);
        builder.Property(request => request.RejectionReason).HasMaxLength(1000);

        // The review queue reads by status, newest first.
        builder.HasIndex(request => new { request.Status, request.SubmittedAt });

        // A tenant's own history, and the "one open request" check.
        builder.HasIndex(request => new { request.TenantId, request.Status, request.SubmittedAt });

        builder.HasOne(request => request.SubscriptionPlan)
            .WithMany()
            .HasForeignKey(request => request.SubscriptionPlanId)
            // A plan that has been paid for must not be deletable out from under the record of
            // that payment.
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Fluent configuration for <see cref="PaymentChannelSetting"/>.</summary>
public sealed class PaymentChannelSettingConfiguration : BaseEntityConfiguration<PaymentChannelSetting>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PaymentChannelSetting> builder)
    {
        builder.ToTable("payment_channel_settings");

        builder.Property(channel => channel.Channel).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(channel => channel.DisplayName).IsRequired().HasMaxLength(80);
        builder.Property(channel => channel.AccountTitle).HasMaxLength(160);
        builder.Property(channel => channel.AccountNumber).HasMaxLength(64);
        builder.Property(channel => channel.BankName).HasMaxLength(120);
        builder.Property(channel => channel.QrStorageKey).HasMaxLength(400);
        builder.Property(channel => channel.QrContentType).HasMaxLength(120);
        builder.Property(channel => channel.Instructions).HasColumnType("text[]").IsRequired();

        // One row per channel. Partial, matching the soft-delete convention.
        builder.HasIndex(channel => channel.Channel)
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="ContactImportExport"/>.</summary>
public sealed class ContactImportExportConfiguration : BaseEntityConfiguration<ContactImportExport>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactImportExport> builder)
    {
        builder.ToTable("contact_import_exports");

        builder.Property(export => export.FileName).IsRequired().HasMaxLength(260);
        builder.Property(export => export.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(export => export.StorageKey).HasMaxLength(400);
        builder.Property(export => export.FailureReason).HasMaxLength(500);

        // The detail screen asks for the newest export of one batch.
        builder.HasIndex(export => new { export.ContactImportBatchId, export.RequestedAt });
    }
}

/// <summary>Fluent configuration for <see cref="ImportJob"/>.</summary>
public sealed class ImportJobConfiguration : BaseEntityConfiguration<ImportJob>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ImportJob> builder)
    {
        builder.ToTable("import_jobs");

        builder.Property(job => job.Kind).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(job => job.State).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(job => job.LastError).HasMaxLength(1000);

        // The claim query's whole plan: it selects pending or expired-lease rows in due order, and
        // runs on every poll across every tenant, so it is the one index that has to be right.
        builder.HasIndex(job => new { job.State, job.AvailableAt });

        builder.HasOne(job => job.ContactImportBatch)
            .WithMany()
            .HasForeignKey(job => job.ContactImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(job => job.ContactImportExport)
            .WithMany()
            .HasForeignKey(job => job.ContactImportExportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fluent configuration for <see cref="ContactImportRow"/>.</summary>
public sealed class ContactImportRowConfiguration : BaseEntityConfiguration<ContactImportRow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContactImportRow> builder)
    {
        builder.ToTable("contact_import_rows");

        builder.Property(row => row.Values).HasColumnType("text[]").IsRequired();
        builder.Property(row => row.Error).HasMaxLength(500);
        builder.Property(row => row.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(row => row.ErrorCode).HasMaxLength(32).HasConversion<string>();
        builder.Property(row => row.ErrorField).HasMaxLength(64);

        // The review table filters a batch's rows by status, and the failed-record export reads
        // only the ones that failed.
        builder.HasIndex(row => new { row.ContactImportBatchId, row.Status });

        // The commit walks a batch in file order, and the preview reads the first few rows.
        builder.HasIndex(row => new { row.ContactImportBatchId, row.RowNumber });
    }
}

/// <summary>Fluent configuration for <see cref="UserToken"/>.</summary>
public sealed class UserTokenConfiguration : BaseEntityConfiguration<UserToken>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<UserToken> builder)
    {
        builder.ToTable("user_tokens");

        // 64 hex characters for a SHA-256 digest.
        builder.Property(token => token.TokenHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(token => token.Purpose).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(token => token.RequestedByIp).HasMaxLength(45);

        // Redemption looks a token up by hash and nothing else, so this index is the whole plan.
        builder.HasIndex(token => token.TokenHash).IsUnique();

        // Supports invalidating a user's outstanding tokens when a new one is issued.
        builder.HasIndex(token => new { token.UserId, token.Purpose, token.ConsumedOn });

        builder.HasOne(token => token.User)
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Fluent configuration for <see cref="CampaignMessage"/>.</summary>
public sealed class CampaignMessageConfiguration : BaseEntityConfiguration<CampaignMessage>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<CampaignMessage> builder)
    {
        builder.ToTable("campaign_messages");

        builder.Property(message => message.PhoneNumber).IsRequired().HasMaxLength(32);
        builder.Property(message => message.Status).IsRequired().HasMaxLength(16).HasConversion<string>();
        builder.Property(message => message.MetaMessageId).HasMaxLength(128);
        builder.Property(message => message.ErrorReason).HasMaxLength(500);

        // One message per contact per firing. Scoped to the run rather than to the campaign: a
        // weekly campaign must reach the same contact every week, which a campaign-wide constraint
        // would forbid outright. Within one firing it still makes a retried dispatch safe, because
        // a second row cannot be created and so a second message cannot be sent.
        builder.HasIndex(message => new { message.CampaignRunId, message.ContactId })
            .IsUnique()
            .HasFilter("campaign_run_id IS NOT NULL AND is_deleted = false");

        // Rows written before campaigns had runs keep the original guarantee. Every new dispatch
        // creates a run, so this covers history rather than anything the dispatcher writes today.
        builder.HasIndex(message => new { message.CampaignId, message.ContactId })
            .IsUnique()
            .HasFilter("campaign_run_id IS NULL AND is_deleted = false");

        // The dispatcher claims the next batch with this index; without it every batch scans the
        // whole campaign.
        builder.HasIndex(message => new { message.CampaignId, message.Status });

        // Webhook receipts arrive keyed by Meta's id and nothing else.
        builder.HasIndex(message => message.MetaMessageId);

        builder.HasOne(message => message.CampaignRun)
            .WithMany(run => run.Messages)
            .HasForeignKey(message => message.CampaignRunId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(message => message.Campaign)
            .WithMany()
            .HasForeignKey(message => message.CampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(message => message.Contact)
            .WithMany()
            .HasForeignKey(message => message.ContactId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Fluent configuration for <see cref="PaymentMethod"/>.</summary>
public sealed class PaymentMethodConfiguration : BaseEntityConfiguration<PaymentMethod>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PaymentMethod> builder)
    {
        builder.ToTable("payment_methods");

        builder.Property(method => method.Kind).IsRequired().HasMaxLength(24).HasConversion<string>();
        builder.Property(method => method.ProviderToken).IsRequired().HasMaxLength(256);
        builder.Property(method => method.Brand).HasMaxLength(40);

        // Four characters, and only ever four. The column is sized so that a full number cannot be
        // stored here even by accident.
        builder.Property(method => method.Last4).HasMaxLength(4);

        // One default per tenant, enforced by the database rather than by convention: two defaults
        // would make "which card do we charge" ambiguous at renewal time.
        builder.HasIndex(method => method.TenantId)
            .IsUnique()
            .HasFilter("is_default = true AND is_deleted = false");

        builder.HasIndex(method => new { method.TenantId, method.ProviderToken })
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}

/// <summary>Fluent configuration for <see cref="BillingProfile"/>.</summary>
public sealed class BillingProfileConfiguration : BaseEntityConfiguration<BillingProfile>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BillingProfile> builder)
    {
        builder.ToTable("billing_profiles");

        builder.Property(profile => profile.CompanyName).HasMaxLength(200);
        builder.Property(profile => profile.AddressLine1).HasMaxLength(200);
        builder.Property(profile => profile.AddressLine2).HasMaxLength(200);
        builder.Property(profile => profile.City).HasMaxLength(120);
        builder.Property(profile => profile.Region).HasMaxLength(120);
        builder.Property(profile => profile.PostalCode).HasMaxLength(24);
        builder.Property(profile => profile.Country).HasMaxLength(2);
        builder.Property(profile => profile.TaxId).HasMaxLength(64);
        builder.Property(profile => profile.BillingEmail).HasMaxLength(254);

        // One per tenant.
        builder.HasIndex(profile => profile.TenantId)
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}
