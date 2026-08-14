using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddManualPayments : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "payment_channel_settings",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                account_title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                account_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                bank_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                qr_storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                qr_content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                instructions = table.Column<List<string>>(type: "text[]", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                tenant_id = table.Column<long>(type: "bigint", nullable: true),
                created_by = table.Column<long>(type: "bigint", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<long>(type: "bigint", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<long>(type: "bigint", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payment_channel_settings", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "payment_requests",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                subscription_plan_id = table.Column<long>(type: "bigint", nullable: false),
                plan_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                billing_cycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                proof_storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                proof_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                proof_content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                proof_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                organisation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                submitted_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                submitted_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                submitted_by_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                reviewed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                reviewed_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                reviewed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                invoice_id = table.Column<long>(type: "bigint", nullable: true),
                tenant_id = table.Column<long>(type: "bigint", nullable: true),
                created_by = table.Column<long>(type: "bigint", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                modified_by = table.Column<long>(type: "bigint", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                deleted_by = table.Column<long>(type: "bigint", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payment_requests", x => x.id);
                table.ForeignKey(
                    name: "fk_payment_requests_invoices_invoice_id",
                    column: x => x.invoice_id,
                    principalTable: "invoices",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "fk_payment_requests_subscription_plans_subscription_plan_id",
                    column: x => x.subscription_plan_id,
                    principalTable: "subscription_plans",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_payment_channel_settings_channel",
            table: "payment_channel_settings",
            column: "channel",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_payment_channel_settings_is_deleted",
            table: "payment_channel_settings",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_payment_requests_invoice_id",
            table: "payment_requests",
            column: "invoice_id");

        migrationBuilder.CreateIndex(
            name: "ix_payment_requests_is_deleted",
            table: "payment_requests",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_payment_requests_status_submitted_at",
            table: "payment_requests",
            columns: new[] { "status", "submitted_at" });

        migrationBuilder.CreateIndex(
            name: "ix_payment_requests_subscription_plan_id",
            table: "payment_requests",
            column: "subscription_plan_id");

        migrationBuilder.CreateIndex(
            name: "ix_payment_requests_tenant_id_id",
            table: "payment_requests",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_payment_requests_tenant_id_status_submitted_at",
            table: "payment_requests",
            columns: new[] { "tenant_id", "status", "submitted_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "payment_channel_settings");

        migrationBuilder.DropTable(
            name: "payment_requests");
    }
}
