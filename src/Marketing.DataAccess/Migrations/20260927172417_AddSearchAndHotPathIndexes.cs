using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddSearchAndHotPathIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_contacts_tenant_id_status_created_on",
            table: "contacts");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_campaign_id_status",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_meta_message_id",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_audit_logs_user_id",
            table: "audit_logs");

        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

        migrationBuilder.CreateIndex(
            name: "ix_conversations_contact_name",
            table: "conversations",
            column: "contact_name")
            .Annotation("Npgsql:IndexMethod", "gin")
            .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

        migrationBuilder.CreateIndex(
            name: "ix_conversations_wa_id",
            table: "conversations",
            column: "wa_id")
            .Annotation("Npgsql:IndexMethod", "gin")
            .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

        migrationBuilder.CreateIndex(
            name: "ix_conversation_messages_direction_occurred_at",
            table: "conversation_messages",
            columns: new[] { "direction", "occurred_at" },
            descending: new[] { false, true },
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contacts_email",
            table: "contacts",
            column: "email")
            .Annotation("Npgsql:IndexMethod", "gin")
            .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

        migrationBuilder.CreateIndex(
            name: "ix_contacts_full_name",
            table: "contacts",
            column: "full_name")
            .Annotation("Npgsql:IndexMethod", "gin")
            .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

        migrationBuilder.CreateIndex(
            name: "ix_contacts_phone_number",
            table: "contacts",
            column: "phone_number")
            .Annotation("Npgsql:IndexMethod", "gin")
            .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_created_on",
            table: "contacts",
            columns: new[] { "tenant_id", "created_on" },
            descending: new[] { false, true },
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_status_created_on",
            table: "contacts",
            columns: new[] { "tenant_id", "status", "created_on" },
            descending: new[] { false, false, true },
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_campaign_id_status_id",
            table: "campaign_messages",
            columns: new[] { "campaign_id", "status", "id" },
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_meta_message_id",
            table: "campaign_messages",
            column: "meta_message_id",
            filter: "meta_message_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_tenant_id_sent_on",
            table: "campaign_messages",
            columns: new[] { "tenant_id", "sent_on" },
            descending: new[] { false, true },
            filter: "sent_on IS NOT NULL AND is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_occurred_on",
            table: "audit_logs",
            column: "occurred_on",

            // An empty array is Npgsql's "every column descends", which for a single-column
            // index is exactly DESC.
            descending: Array.Empty<bool>());

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_user_id_occurred_on",
            table: "audit_logs",
            columns: new[] { "user_id", "occurred_on" },
            descending: new[] { false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_conversations_contact_name",
            table: "conversations");

        migrationBuilder.DropIndex(
            name: "ix_conversations_wa_id",
            table: "conversations");

        migrationBuilder.DropIndex(
            name: "ix_conversation_messages_direction_occurred_at",
            table: "conversation_messages");

        migrationBuilder.DropIndex(
            name: "ix_contacts_email",
            table: "contacts");

        migrationBuilder.DropIndex(
            name: "ix_contacts_full_name",
            table: "contacts");

        migrationBuilder.DropIndex(
            name: "ix_contacts_phone_number",
            table: "contacts");

        migrationBuilder.DropIndex(
            name: "ix_contacts_tenant_id_created_on",
            table: "contacts");

        migrationBuilder.DropIndex(
            name: "ix_contacts_tenant_id_status_created_on",
            table: "contacts");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_campaign_id_status_id",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_meta_message_id",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_campaign_messages_tenant_id_sent_on",
            table: "campaign_messages");

        migrationBuilder.DropIndex(
            name: "ix_audit_logs_occurred_on",
            table: "audit_logs");

        migrationBuilder.DropIndex(
            name: "ix_audit_logs_user_id_occurred_on",
            table: "audit_logs");

        migrationBuilder.AlterDatabase()
            .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

        migrationBuilder.CreateIndex(
            name: "ix_contacts_tenant_id_status_created_on",
            table: "contacts",
            columns: new[] { "tenant_id", "status", "created_on" });

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_campaign_id_status",
            table: "campaign_messages",
            columns: new[] { "campaign_id", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_campaign_messages_meta_message_id",
            table: "campaign_messages",
            column: "meta_message_id");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_user_id",
            table: "audit_logs",
            column: "user_id");
    }
}
