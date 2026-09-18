using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddInboxConversationsAndMedia : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "header_kind",
            table: "message_templates",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateTable(
            name: "conversations",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                wa_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                contact_id = table.Column<long>(type: "bigint", nullable: true),
                contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                last_message_preview = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                last_message_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                unread_count = table.Column<int>(type: "integer", nullable: false),
                window_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
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
                table.PrimaryKey("pk_conversations", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "media_assets",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                meta_media_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                mime_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                size_bytes = table.Column<long>(type: "bigint", nullable: false),
                storage_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                uploaded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
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
                table.PrimaryKey("pk_media_assets", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "conversation_messages",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                conversation_id = table.Column<long>(type: "bigint", nullable: false),
                meta_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                body = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                media_id = table.Column<long>(type: "bigint", nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                template_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
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
                table.PrimaryKey("pk_conversation_messages", x => x.id);
                table.ForeignKey(
                    name: "fk_conversation_messages_conversations_conversation_id",
                    column: x => x.conversation_id,
                    principalTable: "conversations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_conversation_messages_media_asset_media_id",
                    column: x => x.media_id,
                    principalTable: "media_assets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_conversation_messages_conversation_id_occurred_at",
            table: "conversation_messages",
            columns: new[] { "conversation_id", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_conversation_messages_is_deleted",
            table: "conversation_messages",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_conversation_messages_media_id",
            table: "conversation_messages",
            column: "media_id");

        migrationBuilder.CreateIndex(
            name: "ix_conversation_messages_meta_message_id",
            table: "conversation_messages",
            column: "meta_message_id",
            unique: true,
            filter: "meta_message_id IS NOT NULL AND is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_conversation_messages_tenant_id_id",
            table: "conversation_messages",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_conversations_is_deleted",
            table: "conversations",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_conversations_tenant_id_id",
            table: "conversations",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_conversations_tenant_id_last_message_at",
            table: "conversations",
            columns: new[] { "tenant_id", "last_message_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_conversations_tenant_id_wa_id",
            table: "conversations",
            columns: new[] { "tenant_id", "wa_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_media_assets_is_deleted",
            table: "media_assets",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_media_assets_meta_media_id",
            table: "media_assets",
            column: "meta_media_id",
            filter: "meta_media_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_media_assets_tenant_id_id",
            table: "media_assets",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_media_assets_tenant_id_uploaded_at",
            table: "media_assets",
            columns: new[] { "tenant_id", "uploaded_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "conversation_messages");

        migrationBuilder.DropTable(
            name: "conversations");

        migrationBuilder.DropTable(
            name: "media_assets");

        migrationBuilder.DropColumn(
            name: "header_kind",
            table: "message_templates");
    }
}
