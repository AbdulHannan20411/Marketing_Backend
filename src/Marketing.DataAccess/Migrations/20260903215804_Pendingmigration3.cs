using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class Pendingmigration3 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "outbox_emails",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                to_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                to_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                html_body = table.Column<string>(type: "text", nullable: false),
                text_body = table.Column<string>(type: "text", nullable: false),
                reply_to_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                reply_to_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                next_attempt_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                sent_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                table.PrimaryKey("pk_outbox_emails", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_outbox_emails_is_deleted",
            table: "outbox_emails",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_outbox_emails_next_attempt_on",
            table: "outbox_emails",
            column: "next_attempt_on",
            filter: "is_deleted = false AND status = 'Pending'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "outbox_emails");
    }
}
