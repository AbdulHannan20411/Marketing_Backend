using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddTemplateMediaHeaders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<List<string>>(
            name: "body_examples",
            table: "message_templates",
            type: "text[]",
            nullable: false,
            defaultValue: new List<string>());

        migrationBuilder.AddColumn<string>(
            name: "header_example",
            table: "message_templates",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "header_sample_id",
            table: "message_templates",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "header_media_id",
            table: "campaigns",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "header_meta_media_id",
            table: "campaigns",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "header_meta_media_phone_number_id",
            table: "campaigns",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "header_meta_media_uploaded_at",
            table: "campaigns",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "template_header_samples",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                size_bytes = table.Column<long>(type: "bigint", nullable: false),
                storage_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                uploaded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                message_template_id = table.Column<long>(type: "bigint", nullable: true),
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
                table.PrimaryKey("pk_template_header_samples", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_template_header_samples_is_deleted",
            table: "template_header_samples",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_template_header_samples_message_template_id_uploaded_at",
            table: "template_header_samples",
            columns: new[] { "message_template_id", "uploaded_at" });

        migrationBuilder.CreateIndex(
            name: "ix_template_header_samples_tenant_id_id",
            table: "template_header_samples",
            columns: new[] { "tenant_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "template_header_samples");

        migrationBuilder.DropColumn(
            name: "body_examples",
            table: "message_templates");

        migrationBuilder.DropColumn(
            name: "header_example",
            table: "message_templates");

        migrationBuilder.DropColumn(
            name: "header_sample_id",
            table: "message_templates");

        migrationBuilder.DropColumn(
            name: "header_media_id",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "header_meta_media_id",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "header_meta_media_phone_number_id",
            table: "campaigns");

        migrationBuilder.DropColumn(
            name: "header_meta_media_uploaded_at",
            table: "campaigns");
    }
}
