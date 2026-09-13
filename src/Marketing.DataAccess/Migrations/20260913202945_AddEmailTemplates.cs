using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddEmailTemplates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "email_templates",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                html_body = table.Column<string>(type: "text", nullable: false),
                text_body = table.Column<string>(type: "text", nullable: false),
                variables_json = table.Column<string>(type: "jsonb", nullable: false),
                default_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                updated_on = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                updated_by_user_id = table.Column<long>(type: "bigint", nullable: true),
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
                table.PrimaryKey("pk_email_templates", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_email_templates_is_deleted",
            table: "email_templates",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_email_templates_key",
            table: "email_templates",
            column: "key",
            unique: true,
            filter: "is_deleted = false");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "email_templates");
    }
}
