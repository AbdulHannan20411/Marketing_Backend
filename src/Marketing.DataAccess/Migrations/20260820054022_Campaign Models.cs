using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class CampaignModels : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "campaign_recipients");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_campaign_id_contact_id",
            table: "campaign_messages");

        migrationBuilder.AddColumn<long>(
            name: "active_campaign_run_id",
            table: "campaigns",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "triggered_manually",
            table: "campaign_runs",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "campaign_run_id",
            table: "campaign_messages",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_campaign_id_contact_id",
            table: "campaign_messages",
            columns: new[] { "campaign_id", "contact_id" },
            unique: true,
            filter: "campaign_run_id IS NULL AND is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_campaign_run_id_contact_id",
            table: "campaign_messages",
            columns: new[] { "campaign_run_id", "contact_id" },
            unique: true,
            filter: "campaign_run_id IS NOT NULL AND is_deleted = false");

        migrationBuilder.AddForeignKey(
            name: "fk_campaign_messages_campaign_runs_campaign_run_id",
            table: "campaign_messages",
            column: "campaign_run_id",
            principalTable: "campaign_runs",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_campaign_messages_campaign_runs_campaign_run_id",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_campaign_id_contact_id",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_campaign_run_id_contact_id",
            table: "campaign_messages");

        migrationBuilder.DropColumn(
            name: "active_campaign_run_id",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "triggered_manually",
            table: "campaign_runs");

        migrationBuilder.DropColumn(
            name: "campaign_run_id",
            table: "campaign_messages");

        migrationBuilder.CreateTable(
            name: "campaign_recipients",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                campaign_run_id = table.Column<long>(type: "bigint", nullable: false),
                contact_id = table.Column<long>(type: "bigint", nullable: false),
                created_by = table.Column<long>(type: "bigint", nullable: false),
                created_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                deleted_by = table.Column<long>(type: "bigint", nullable: true),
                deleted_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                meta_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                modified_by = table.Column<long>(type: "bigint", nullable: true),
                modified_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                tenant_id = table.Column<long>(type: "bigint", nullable: true)
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
            name: "ix_campaign_messages_campaign_id_contact_id",
            table: "campaign_messages",
            columns: new[] { "campaign_id", "contact_id" },
            unique: true,
            filter: "is_deleted = false");

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
    }
}
