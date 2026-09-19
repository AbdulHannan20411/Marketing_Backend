using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddMultipleWhatsAppNumbers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_whatsapp_connections_tenant_id",
            table: "whatsapp_connections");

        migrationBuilder.DropIndex(
            name: "ix_message_templates_tenant_id_name_language",
            table: "message_templates");

        migrationBuilder.DropIndex(
            name: "ix_conversations_tenant_id_wa_id",
            table: "conversations");

        migrationBuilder.AddColumn<string>(
            name: "account_status",
            table: "whatsapp_connections",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "api_status",
            table: "whatsapp_connections",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<bool>(
            name: "is_default",
            table: "whatsapp_connections",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "label",
            table: "whatsapp_connections",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "last_error",
            table: "whatsapp_connections",
            type: "character varying(300)",
            maxLength: 300,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_message_received_at",
            table: "whatsapp_connections",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_message_sent_at",
            table: "whatsapp_connections",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_webhook_at",
            table: "whatsapp_connections",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "phone_number_status",
            table: "whatsapp_connections",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "default_whats_app_connection_id",
            table: "users",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "assigned_to_user_id",
            table: "conversations",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "whats_app_connection_id",
            table: "conversations",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "whats_app_connection_id",
            table: "campaigns",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "whatsapp_account_access",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                whats_app_connection_id = table.Column<long>(type: "bigint", nullable: false),
                can_view = table.Column<bool>(type: "boolean", nullable: false),
                can_reply = table.Column<bool>(type: "boolean", nullable: false),
                can_broadcast = table.Column<bool>(type: "boolean", nullable: false),
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
                table.PrimaryKey("pk_whatsapp_account_access", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_connections_tenant_id_default",
            table: "whatsapp_connections",
            column: "tenant_id",
            unique: true,
            filter: "is_default = true AND is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_message_templates_tenant_id_waba_id_name_language",
            table: "message_templates",
            columns: new[] { "tenant_id", "waba_id", "name", "language" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_conversations_tenant_id_assigned_to_user_id",
            table: "conversations",
            columns: new[] { "tenant_id", "assigned_to_user_id" });

        migrationBuilder.CreateIndex(
            name: "ix_conversations_tenant_id_whats_app_connection_id_wa_id",
            table: "conversations",
            columns: new[] { "tenant_id", "whats_app_connection_id", "wa_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaigns_tenant_id_whats_app_connection_id",
            table: "campaigns",
            columns: new[] { "tenant_id", "whats_app_connection_id" });

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_account_access_is_deleted",
            table: "whatsapp_account_access",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_account_access_tenant_id_id",
            table: "whatsapp_account_access",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_account_access_user_id_whats_app_connection_id",
            table: "whatsapp_account_access",
            columns: new[] { "user_id", "whats_app_connection_id" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_account_access_whats_app_connection_id",
            table: "whatsapp_account_access",
            column: "whats_app_connection_id");

        // The number every workspace already has becomes its first account: named after the business
        // Meta verified, and made the default, so everything unaware of multiple numbers carries on
        // exactly as before.
        migrationBuilder.Sql(
            """
            UPDATE whatsapp_connections
            SET label = LEFT(COALESCE(NULLIF(verified_name, ''), NULLIF(display_phone_number, ''), 'WhatsApp'), 40),
                api_status = 'unknown'
            WHERE label = '';

            UPDATE whatsapp_connections AS c
            SET is_default = true
            WHERE c.is_deleted = false
              AND c.id = (
                  SELECT x.id FROM whatsapp_connections AS x
                  WHERE x.tenant_id = c.tenant_id AND x.is_deleted = false
                  ORDER BY (x.status = 'Disconnected'), x.id
                  LIMIT 1);

            -- Existing threads and campaigns belong to that number: it is the only one they could
            -- have been written to or sent from.
            UPDATE conversations AS v
            SET whats_app_connection_id = (
                SELECT c.id FROM whatsapp_connections AS c
                WHERE c.tenant_id = v.tenant_id
                ORDER BY c.is_deleted, c.is_default DESC, c.id
                LIMIT 1)
            WHERE v.whats_app_connection_id IS NULL;

            UPDATE campaigns AS m
            SET whats_app_connection_id = (
                SELECT c.id FROM whatsapp_connections AS c
                WHERE c.tenant_id = m.tenant_id
                ORDER BY c.is_deleted, c.is_default DESC, c.id
                LIMIT 1)
            WHERE m.whats_app_connection_id IS NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "whatsapp_account_access");

        migrationBuilder.DropIndex(
            name: "ix_whatsapp_connections_tenant_id_default",
            table: "whatsapp_connections");

        migrationBuilder.DropIndex(
            name: "ix_message_templates_tenant_id_waba_id_name_language",
            table: "message_templates");

        migrationBuilder.DropIndex(
            name: "ix_conversations_tenant_id_assigned_to_user_id",
            table: "conversations");

        migrationBuilder.DropIndex(
            name: "ix_conversations_tenant_id_whats_app_connection_id_wa_id",
            table: "conversations");

        migrationBuilder.DropIndex(
            name: "ix_campaigns_tenant_id_whats_app_connection_id",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "account_status",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "api_status",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "is_default",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "label",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "last_error",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "last_message_received_at",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "last_message_sent_at",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "last_webhook_at",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "phone_number_status",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "default_whats_app_connection_id",
            table: "users");

        migrationBuilder.DropColumn(
            name: "assigned_to_user_id",
            table: "conversations");

        migrationBuilder.DropColumn(
            name: "whats_app_connection_id",
            table: "conversations");

        migrationBuilder.DropColumn(
            name: "whats_app_connection_id",
            table: "campaigns");

        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_connections_tenant_id",
            table: "whatsapp_connections",
            column: "tenant_id",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_message_templates_tenant_id_name_language",
            table: "message_templates",
            columns: new[] { "tenant_id", "name", "language" },
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_conversations_tenant_id_wa_id",
            table: "conversations",
            columns: new[] { "tenant_id", "wa_id" },
            unique: true,
            filter: "is_deleted = false");
    }
}
