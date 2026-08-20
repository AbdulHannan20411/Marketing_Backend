using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddCampaignRecurrenceAndRuns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "description",
            table: "campaigns",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_run_at_utc",
            table: "campaigns",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "next_run_at_utc",
            table: "campaigns",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "occurrences_run",
            table: "campaigns",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "recurrence_json",
            table: "campaigns",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "time_zone",
            table: "campaigns",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "campaign_runs",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                campaign_id = table.Column<long>(type: "bigint", nullable: false),
                occurrence_number = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                scheduled_for_utc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                audience_size = table.Column<int>(type: "integer", nullable: false),
                sent_count = table.Column<int>(type: "integer", nullable: false),
                delivered_count = table.Column<int>(type: "integer", nullable: false),
                read_count = table.Column<int>(type: "integer", nullable: false),
                clicked_count = table.Column<int>(type: "integer", nullable: false),
                failed_count = table.Column<int>(type: "integer", nullable: false),
                skipped_count = table.Column<int>(type: "integer", nullable: false),
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
                table.PrimaryKey("pk_campaign_runs", x => x.id);
                table.ForeignKey(
                    name: "fk_campaign_runs_campaigns_campaign_id",
                    column: x => x.campaign_id,
                    principalTable: "campaigns",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "campaign_recipients",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                campaign_run_id = table.Column<long>(type: "bigint", nullable: false),
                contact_id = table.Column<long>(type: "bigint", nullable: false),
                phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                meta_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                table.PrimaryKey("pk_campaign_recipients", x => x.id);
                table.ForeignKey(
                    name: "fk_campaign_recipients_campaign_runs_campaign_run_id",
                    column: x => x.campaign_run_id,
                    principalTable: "campaign_runs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_next_run_at_utc",
            table: "campaigns",
            column: "next_run_at_utc",
            filter: "next_run_at_utc IS NOT NULL AND is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_recipients_campaign_run_id_contact_id",
            table: "campaign_recipients",
            columns: new[] { "campaign_run_id", "contact_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_recipients_is_deleted",
            table: "campaign_recipients",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_recipients_meta_message_id",
            table: "campaign_recipients",
            column: "meta_message_id",
            filter: "meta_message_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_recipients_tenant_id_id",
            table: "campaign_recipients",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_campaign_runs_campaign_id_occurrence_number",
            table: "campaign_runs",
            columns: new[] { "campaign_id", "occurrence_number" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_runs_campaign_id_scheduled_for_utc",
            table: "campaign_runs",
            columns: new[] { "campaign_id", "scheduled_for_utc" });

        migrationBuilder.CreateIndex(
            name: "ix_campaign_runs_is_deleted",
            table: "campaign_runs",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_runs_tenant_id_id",
            table: "campaign_runs",
            columns: new[] { "tenant_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "campaign_recipients");

        migrationBuilder.DropTable(
            name: "campaign_runs");

        migrationBuilder.DropIndex(
            name: "ix_campaigns_next_run_at_utc",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "description",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "last_run_at_utc",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "next_run_at_utc",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "occurrences_run",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "recurrence_json",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "time_zone",
            table: "campaigns");
    }
}
