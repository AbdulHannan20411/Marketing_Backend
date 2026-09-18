using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddAiAutoReply : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // An empty array, not null: every existing plan sells no automatic-reply triggers until a
        // Super Admin says otherwise, and the column is not nullable - without the default this
        // migration fails on any database that already has plans in it.
        migrationBuilder.AddColumn<List<string>>(
            name: "auto_reply_triggers",
            table: "subscription_plans",
            type: "text[]",
            nullable: false,
            defaultValue: new List<string>());

        migrationBuilder.AddColumn<int>(
            name: "monthly_ai_reply_limit",
            table: "subscription_plans",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "is_auto_reply",
            table: "conversation_messages",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "auto_reply_settings",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                greeting_enabled = table.Column<bool>(type: "boolean", nullable: false),
                first_message_enabled = table.Column<bool>(type: "boolean", nullable: false),
                unanswered_enabled = table.Column<bool>(type: "boolean", nullable: false),
                delay_seconds = table.Column<int>(type: "integer", nullable: false),
                unanswered_after_minutes = table.Column<int>(type: "integer", nullable: false),
                instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                max_per_conversation_per_day = table.Column<int>(type: "integer", nullable: false),
                quota_notice_sent_for_period_end = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
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
                table.PrimaryKey("pk_auto_reply_settings", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_settings_is_deleted",
            table: "auto_reply_settings",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_settings_tenant_id",
            table: "auto_reply_settings",
            column: "tenant_id",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_settings_tenant_id_id",
            table: "auto_reply_settings",
            columns: new[] { "tenant_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "auto_reply_settings");

        migrationBuilder.DropColumn(
            name: "auto_reply_triggers",
            table: "subscription_plans");

        migrationBuilder.DropColumn(
            name: "monthly_ai_reply_limit",
            table: "subscription_plans");

        migrationBuilder.DropColumn(
            name: "is_auto_reply",
            table: "conversation_messages");
    }
}
