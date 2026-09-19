using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddAutoReplyKnowledge : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "knowledge_fallback",
            table: "auto_reply_settings",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "handoff");

        migrationBuilder.AddColumn<string>(
            name: "knowledge_fallback_message",
            table: "auto_reply_settings",
            type: "character varying(300)",
            maxLength: 300,
            nullable: false,
            defaultValue: "Thanks for your message! Someone from our team will get back to you shortly.");

        migrationBuilder.AddColumn<string>(
            name: "knowledge_source_file_name",
            table: "auto_reply_settings",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "knowledge_updated_at",
            table: "auto_reply_settings",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "knowledge_updated_by_user_id",
            table: "auto_reply_settings",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "auto_reply_attempts",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                conversation_id = table.Column<long>(type: "bigint", nullable: false),
                inbound_message_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                fallback_sent = table.Column<bool>(type: "boolean", nullable: false),
                question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                attempted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
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
                table.PrimaryKey("pk_auto_reply_attempts", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "auto_reply_knowledge_entries",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                answer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                price = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                available = table.Column<bool>(type: "boolean", nullable: true),
                keywords = table.Column<List<string>>(type: "text[]", nullable: false),
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
                table.PrimaryKey("pk_auto_reply_knowledge_entries", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_attempts_conversation_id_inbound_message_at",
            table: "auto_reply_attempts",
            columns: new[] { "conversation_id", "inbound_message_at" });

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_attempts_is_deleted",
            table: "auto_reply_attempts",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_attempts_tenant_id_id",
            table: "auto_reply_attempts",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_attempts_tenant_id_outcome_attempted_at",
            table: "auto_reply_attempts",
            columns: new[] { "tenant_id", "outcome", "attempted_at" });

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_knowledge_entries_is_deleted",
            table: "auto_reply_knowledge_entries",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_knowledge_entries_tenant_id_id",
            table: "auto_reply_knowledge_entries",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_auto_reply_knowledge_entries_tenant_id_sort_order",
            table: "auto_reply_knowledge_entries",
            columns: new[] { "tenant_id", "sort_order" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "auto_reply_attempts");

        migrationBuilder.DropTable(
            name: "auto_reply_knowledge_entries");

        migrationBuilder.DropColumn(
            name: "knowledge_fallback",
            table: "auto_reply_settings");

        migrationBuilder.DropColumn(
            name: "knowledge_fallback_message",
            table: "auto_reply_settings");

        migrationBuilder.DropColumn(
            name: "knowledge_source_file_name",
            table: "auto_reply_settings");

        migrationBuilder.DropColumn(
            name: "knowledge_updated_at",
            table: "auto_reply_settings");

        migrationBuilder.DropColumn(
            name: "knowledge_updated_by_user_id",
            table: "auto_reply_settings");
    }
}
