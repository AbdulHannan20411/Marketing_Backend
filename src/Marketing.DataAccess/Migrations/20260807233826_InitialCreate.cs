using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "activity_entries",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                actor = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                occurred_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_activity_entries", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "audit_logs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                entity_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                changes = table.Column<string>(type: "jsonb", nullable: true),
                correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                occurred_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_audit_logs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "contact_groups",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contact_groups", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "contact_import_batches",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                columns = table.Column<List<string>>(type: "text[]", nullable: false),
                total_rows = table.Column<int>(type: "integer", nullable: false),
                duplicate_rows = table.Column<int>(type: "integer", nullable: false),
                invalid_rows = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                uploaded_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                committed_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                imported_count = table.Column<int>(type: "integer", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contact_import_batches", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "contact_tags",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contact_tags", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "contacts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                normalized_phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                opted_in_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                last_messaged_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contacts", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "invoices",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                plan_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                billing_cycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                tax = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                issued_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                due_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                paid_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                period_start = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                period_end = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_invoices", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "message_daily_stats",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                date = table.Column<DateOnly>(type: "date", nullable: false),
                sent = table.Column<int>(type: "integer", nullable: false),
                delivered = table.Column<int>(type: "integer", nullable: false),
                read = table.Column<int>(type: "integer", nullable: false),
                clicked = table.Column<int>(type: "integer", nullable: false),
                failed = table.Column<int>(type: "integer", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_message_daily_stats", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "message_templates",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                meta_template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                header_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                body_text = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                footer_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                variables = table.Column<List<string>>(type: "text[]", nullable: false),
                buttons = table.Column<List<string>>(type: "text[]", nullable: false),
                quality_score = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                times_used = table.Column<int>(type: "integer", nullable: false),
                rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_message_templates", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: true),
                kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                icon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                read = table.Column<bool>(type: "boolean", nullable: false),
                action_label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                action_route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                occurred_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_notifications", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "permission_sets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                is_system = table.Column<bool>(type: "boolean", nullable: false),
                permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_permission_sets", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "renewal_records",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                plan_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                billing_cycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                renewed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                period_end = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                automatic = table.Column<bool>(type: "boolean", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_renewal_records", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "roles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                normalized_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                is_system_role = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_roles", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "subscription_plans",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                tagline = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                monthly_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                yearly_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                trial_days = table.Column<int>(type: "integer", nullable: false),
                renewal_period_months = table.Column<int>(type: "integer", nullable: false),
                discount_percent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                is_promotional = table.Column<bool>(type: "boolean", nullable: false),
                is_most_popular = table.Column<bool>(type: "boolean", nullable: false),
                is_recommended = table.Column<bool>(type: "boolean", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                support_level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                enabled_modules = table.Column<List<string>>(type: "text[]", nullable: false),
                highlights = table.Column<List<string>>(type: "text[]", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                max_employees = table.Column<int>(type: "integer", nullable: true),
                max_contacts = table.Column<int>(type: "integer", nullable: true),
                max_campaigns = table.Column<int>(type: "integer", nullable: true),
                max_whats_app_accounts = table.Column<int>(type: "integer", nullable: true),
                max_email_accounts = table.Column<int>(type: "integer", nullable: true),
                max_social_accounts = table.Column<int>(type: "integer", nullable: true),
                max_api_calls_per_month = table.Column<int>(type: "integer", nullable: true),
                max_storage_mb = table.Column<int>(type: "integer", nullable: true),
                daily_message_limit = table.Column<int>(type: "integer", nullable: true),
                monthly_message_limit = table.Column<int>(type: "integer", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_subscription_plans", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tenants",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Pending"),
                contact_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "UTC"),
                currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "USD"),
                contact_quota = table.Column<int>(type: "integer", nullable: false),
                monthly_message_quota = table.Column<int>(type: "integer", nullable: false),
                activated_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                suspended_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                plan_band = table.Column<int>(type: "integer", nullable: false),
                last_active_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                messages_this_month = table.Column<int>(type: "integer", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tenants", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "whatsapp_connections",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                waba_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                phone_number_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                display_phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                verified_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                business_profile_about = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                business_category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                quality_rating = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                messaging_limit = table.Column<int>(type: "integer", nullable: false),
                messages_last24h = table.Column<int>(type: "integer", nullable: false),
                connected_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                webhook_healthy = table.Column<bool>(type: "boolean", nullable: false),
                template_namespace_alias = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                encrypted_access_token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                token_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_whatsapp_connections", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "contact_import_rows",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                contact_import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                row_number = table.Column<int>(type: "integer", nullable: false),
                values = table.Column<List<string>>(type: "text[]", nullable: false),
                is_duplicate = table.Column<bool>(type: "boolean", nullable: false),
                error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contact_import_rows", x => x.id);
                table.ForeignKey(
                    name: "fk_contact_import_rows_contact_import_batches_contact_import_b",
                    column: x => x.contact_import_batch_id,
                    principalTable: "contact_import_batches",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "contact_group_members",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                contact_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contact_group_members", x => x.id);
                table.ForeignKey(
                    name: "fk_contact_group_members_contact_groups_contact_group_id",
                    column: x => x.contact_group_id,
                    principalTable: "contact_groups",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_contact_group_members_contacts_contact_id",
                    column: x => x.contact_id,
                    principalTable: "contacts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "contact_tag_assignments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                contact_tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_contact_tag_assignments", x => x.id);
                table.ForeignKey(
                    name: "fk_contact_tag_assignments_contact_tags_contact_tag_id",
                    column: x => x.contact_tag_id,
                    principalTable: "contact_tags",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_contact_tag_assignments_contacts_contact_id",
                    column: x => x.contact_id,
                    principalTable: "contacts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "payments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                invoice_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                card_brand = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                processed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payments", x => x.id);
                table.ForeignKey(
                    name: "fk_payments_invoices_invoice_id",
                    column: x => x.invoice_id,
                    principalTable: "invoices",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "campaigns",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                template_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                message_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                audience_label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                audience_size = table.Column<int>(type: "integer", nullable: false),
                sent_count = table.Column<int>(type: "integer", nullable: false),
                delivered_count = table.Column<int>(type: "integer", nullable: false),
                read_count = table.Column<int>(type: "integer", nullable: false),
                clicked_count = table.Column<int>(type: "integer", nullable: false),
                failed_count = table.Column<int>(type: "integer", nullable: false),
                audience_group_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                scheduled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                recipients_queued_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                resume_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                created_by_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_campaigns", x => x.id);
                table.ForeignKey(
                    name: "fk_campaigns_message_templates_message_template_id",
                    column: x => x.message_template_id,
                    principalTable: "message_templates",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "tenant_subscriptions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                subscription_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                billing_cycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                current_period_start = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                current_period_end = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                next_renewal_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                auto_renew = table.Column<bool>(type: "boolean", nullable: false),
                trial_ends_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                seats_purchased = table.Column<int>(type: "integer", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tenant_subscriptions", x => x.id);
                table.ForeignKey(
                    name: "fk_tenant_subscriptions_subscription_plans_subscription_plan_id",
                    column: x => x.subscription_plan_id,
                    principalTable: "subscription_plans",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                display_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Invited"),
                security_stamp = table.Column<Guid>(type: "uuid", nullable: false),
                email_confirmed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                last_login_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                failed_login_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                lockout_ends_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                job_title = table.Column<string>(type: "text", nullable: false),
                avatar_url = table.Column<string>(type: "text", nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", x => x.id);
                table.ForeignKey(
                    name: "fk_users_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "campaign_messages",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                meta_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                sent_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                delivered_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                read_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                error_code = table.Column<int>(type: "integer", nullable: true),
                error_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_campaign_messages", x => x.id);
                table.ForeignKey(
                    name: "fk_campaign_messages_campaigns_campaign_id",
                    column: x => x.campaign_id,
                    principalTable: "campaigns",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_campaign_messages_contacts_contact_id",
                    column: x => x.contact_id,
                    principalTable: "contacts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "delivery_failures",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                campaign_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                error_code = table.Column<int>(type: "integer", nullable: false),
                occurred_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_delivery_failures", x => x.id);
                table.ForeignKey(
                    name: "fk_delivery_failures_campaigns_campaign_id",
                    column: x => x.campaign_id,
                    principalTable: "campaigns",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "refresh_tokens",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                security_stamp = table.Column<Guid>(type: "uuid", nullable: false),
                expires_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                consumed_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                revoked_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                revoked_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                replaced_by_token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                created_by_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_refresh_tokens", x => x.id);
                table.ForeignKey(
                    name: "fk_refresh_tokens_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_permission_overrides",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                is_granted = table.Column<bool>(type: "boolean", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_permission_overrides", x => x.id);
                table.ForeignKey(
                    name: "fk_user_permission_overrides_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_roles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_roles", x => x.id);
                table.ForeignKey(
                    name: "fk_user_roles_roles_role_id",
                    column: x => x.role_id,
                    principalTable: "roles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_user_roles_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_tokens",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                expires_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                consumed_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                requested_by_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_tokens", x => x.id);
                table.ForeignKey(
                    name: "fk_user_tokens_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_activity_entries_is_deleted",
            table: "activity_entries",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_activity_entries_tenant_id_id",
            table: "activity_entries",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_activity_entries_tenant_id_occurred_on",
            table: "activity_entries",
            columns: new[] { "tenant_id", "occurred_on" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_entity_name_entity_id",
            table: "audit_logs",
            columns: new[] { "entity_name", "entity_id" });

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_tenant_id_occurred_on",
            table: "audit_logs",
            columns: new[] { "tenant_id", "occurred_on" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_user_id",
            table: "audit_logs",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_campaign_id_contact_id",
            table: "campaign_messages",
            columns: new[] { "campaign_id", "contact_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_campaign_id_status",
            table: "campaign_messages",
            columns: new[] { "campaign_id", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_contact_id",
            table: "campaign_messages",
            column: "contact_id");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_is_deleted",
            table: "campaign_messages",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_meta_message_id",
            table: "campaign_messages",
            column: "meta_message_id");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_tenant_id_id",
            table: "campaign_messages",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_is_deleted",
            table: "campaigns",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_message_template_id",
            table: "campaigns",
            column: "message_template_id");

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_status_scheduled_at",
            table: "campaigns",
            columns: new[] { "status", "scheduled_at" });

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_tenant_id_id",
            table: "campaigns",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_tenant_id_status_created_on",
            table: "campaigns",
            columns: new[] { "tenant_id", "status", "created_on" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_group_members_contact_group_id_contact_id",
            table: "contact_group_members",
            columns: new[] { "contact_group_id", "contact_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_group_members_contact_id",
            table: "contact_group_members",
            column: "contact_id");

        migrationBuilder.CreateIndex(
            name: "ix_contact_group_members_is_deleted",
            table: "contact_group_members",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_group_members_tenant_id_id",
            table: "contact_group_members",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_groups_is_deleted",
            table: "contact_groups",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_groups_tenant_id_id",
            table: "contact_groups",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_groups_tenant_id_name",
            table: "contact_groups",
            columns: new[] { "tenant_id", "name" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_batches_is_deleted",
            table: "contact_import_batches",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_batches_tenant_id_id",
            table: "contact_import_batches",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_batches_tenant_id_status_uploaded_on",
            table: "contact_import_batches",
            columns: new[] { "tenant_id", "status", "uploaded_on" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_rows_contact_import_batch_id_row_number",
            table: "contact_import_rows",
            columns: new[] { "contact_import_batch_id", "row_number" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_rows_is_deleted",
            table: "contact_import_rows",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_rows_tenant_id_id",
            table: "contact_import_rows",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_tag_assignments_contact_id",
            table: "contact_tag_assignments",
            column: "contact_id");

        migrationBuilder.CreateIndex(
            name: "ix_contact_tag_assignments_contact_tag_id_contact_id",
            table: "contact_tag_assignments",
            columns: new[] { "contact_tag_id", "contact_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_tag_assignments_is_deleted",
            table: "contact_tag_assignments",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_tag_assignments_tenant_id_id",
            table: "contact_tag_assignments",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_tags_is_deleted",
            table: "contact_tags",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_tags_tenant_id_id",
            table: "contact_tags",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_tags_tenant_id_name",
            table: "contact_tags",
            columns: new[] { "tenant_id", "name" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contacts_is_deleted",
            table: "contacts",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_id",
            table: "contacts",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_lifecycle",
            table: "contacts",
            columns: new[] { "tenant_id", "lifecycle" });

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_normalized_phone_number",
            table: "contacts",
            columns: new[] { "tenant_id", "normalized_phone_number" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_status_created_on",
            table: "contacts",
            columns: new[] { "tenant_id", "status", "created_on" });

        migrationBuilder.CreateIndex(
            name: "ix_delivery_failures_campaign_id",
            table: "delivery_failures",
            column: "campaign_id");

        migrationBuilder.CreateIndex(
            name: "ix_delivery_failures_is_deleted",
            table: "delivery_failures",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_delivery_failures_tenant_id_id",
            table: "delivery_failures",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_delivery_failures_tenant_id_occurred_on",
            table: "delivery_failures",
            columns: new[] { "tenant_id", "occurred_on" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_invoices_is_deleted",
            table: "invoices",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_invoices_number",
            table: "invoices",
            column: "number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_invoices_tenant_id_id",
            table: "invoices",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_invoices_tenant_id_issued_at",
            table: "invoices",
            columns: new[] { "tenant_id", "issued_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_message_daily_stats_is_deleted",
            table: "message_daily_stats",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_message_daily_stats_tenant_id_date",
            table: "message_daily_stats",
            columns: new[] { "tenant_id", "date" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_message_daily_stats_tenant_id_id",
            table: "message_daily_stats",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_message_templates_is_deleted",
            table: "message_templates",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_message_templates_tenant_id_id",
            table: "message_templates",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_message_templates_tenant_id_name_language",
            table: "message_templates",
            columns: new[] { "tenant_id", "name", "language" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_notifications_is_deleted",
            table: "notifications",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_notifications_tenant_id_id",
            table: "notifications",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_notifications_tenant_id_user_id_occurred_on",
            table: "notifications",
            columns: new[] { "tenant_id", "user_id", "occurred_on" },
            descending: new[] { false, false, true });

        migrationBuilder.CreateIndex(
            name: "ix_payments_invoice_id",
            table: "payments",
            column: "invoice_id");

        migrationBuilder.CreateIndex(
            name: "ix_payments_is_deleted",
            table: "payments",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_payments_tenant_id_id",
            table: "payments",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_payments_tenant_id_processed_at",
            table: "payments",
            columns: new[] { "tenant_id", "processed_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_permission_sets_is_deleted",
            table: "permission_sets",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_permission_sets_tenant_id_id",
            table: "permission_sets",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_permission_sets_tenant_id_name",
            table: "permission_sets",
            columns: new[] { "tenant_id", "name" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_expires_on",
            table: "refresh_tokens",
            column: "expires_on");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_is_deleted",
            table: "refresh_tokens",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_tenant_id_id",
            table: "refresh_tokens",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_token_hash",
            table: "refresh_tokens",
            column: "token_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_user_id_session_id",
            table: "refresh_tokens",
            columns: new[] { "user_id", "session_id" });

        migrationBuilder.CreateIndex(
            name: "ix_renewal_records_is_deleted",
            table: "renewal_records",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_renewal_records_tenant_id_id",
            table: "renewal_records",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_renewal_records_tenant_id_renewed_at",
            table: "renewal_records",
            columns: new[] { "tenant_id", "renewed_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_roles_is_deleted",
            table: "roles",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_roles_normalized_name",
            table: "roles",
            column: "normalized_name",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_subscription_plans_is_deleted",
            table: "subscription_plans",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_subscription_plans_status_sort_order",
            table: "subscription_plans",
            columns: new[] { "status", "sort_order" });

        migrationBuilder.CreateIndex(
            name: "ix_tenant_subscriptions_is_deleted",
            table: "tenant_subscriptions",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_tenant_subscriptions_subscription_plan_id",
            table: "tenant_subscriptions",
            column: "subscription_plan_id");

        migrationBuilder.CreateIndex(
            name: "ix_tenant_subscriptions_tenant_id",
            table: "tenant_subscriptions",
            column: "tenant_id",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_tenant_subscriptions_tenant_id_id",
            table: "tenant_subscriptions",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_tenants_is_deleted",
            table: "tenants",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_tenants_slug",
            table: "tenants",
            column: "slug",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_tenants_status",
            table: "tenants",
            column: "status");

        migrationBuilder.CreateIndex(
            name: "ix_user_permission_overrides_is_deleted",
            table: "user_permission_overrides",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_permission_overrides_tenant_id_id",
            table: "user_permission_overrides",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_user_permission_overrides_user_id_permission",
            table: "user_permission_overrides",
            columns: new[] { "user_id", "permission" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_roles_is_deleted",
            table: "user_roles",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_roles_role_id",
            table: "user_roles",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "ix_user_roles_tenant_id_id",
            table: "user_roles",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_user_roles_user_id_role_id",
            table: "user_roles",
            columns: new[] { "user_id", "role_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_tokens_is_deleted",
            table: "user_tokens",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_tokens_tenant_id_id",
            table: "user_tokens",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_user_tokens_token_hash",
            table: "user_tokens",
            column: "token_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_user_tokens_user_id_purpose_consumed_on",
            table: "user_tokens",
            columns: new[] { "user_id", "purpose", "consumed_on" });

        migrationBuilder.CreateIndex(
            name: "ix_users_is_deleted",
            table: "users",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_users_normalized_email",
            table: "users",
            column: "normalized_email",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_users_tenant_id_id",
            table: "users",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_users_tenant_id_status",
            table: "users",
            columns: new[] { "tenant_id", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_connections_is_deleted",
            table: "whatsapp_connections",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_connections_tenant_id",
            table: "whatsapp_connections",
            column: "tenant_id",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_connections_tenant_id_id",
            table: "whatsapp_connections",
            columns: new[] { "tenant_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "activity_entries");

        migrationBuilder.DropTable(
            name: "audit_logs");

        migrationBuilder.DropTable(
            name: "campaign_messages");

        migrationBuilder.DropTable(
            name: "contact_group_members");

        migrationBuilder.DropTable(
            name: "contact_import_rows");

        migrationBuilder.DropTable(
            name: "contact_tag_assignments");

        migrationBuilder.DropTable(
            name: "delivery_failures");

        migrationBuilder.DropTable(
            name: "message_daily_stats");

        migrationBuilder.DropTable(
            name: "notifications");

        migrationBuilder.DropTable(
            name: "payments");

        migrationBuilder.DropTable(
            name: "permission_sets");

        migrationBuilder.DropTable(
            name: "refresh_tokens");

        migrationBuilder.DropTable(
            name: "renewal_records");

        migrationBuilder.DropTable(
            name: "tenant_subscriptions");

        migrationBuilder.DropTable(
            name: "user_permission_overrides");

        migrationBuilder.DropTable(
            name: "user_roles");

        migrationBuilder.DropTable(
            name: "user_tokens");

        migrationBuilder.DropTable(
            name: "whatsapp_connections");

        migrationBuilder.DropTable(
            name: "contact_groups");

        migrationBuilder.DropTable(
            name: "contact_import_batches");

        migrationBuilder.DropTable(
            name: "contact_tags");

        migrationBuilder.DropTable(
            name: "contacts");

        migrationBuilder.DropTable(
            name: "campaigns");

        migrationBuilder.DropTable(
            name: "invoices");

        migrationBuilder.DropTable(
            name: "subscription_plans");

        migrationBuilder.DropTable(
            name: "roles");

        migrationBuilder.DropTable(
            name: "users");

        migrationBuilder.DropTable(
            name: "message_templates");

        migrationBuilder.DropTable(
            name: "tenants");
    }
}
