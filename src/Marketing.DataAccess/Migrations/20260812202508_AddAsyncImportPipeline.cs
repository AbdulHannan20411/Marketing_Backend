using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddAsyncImportPipeline : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "error_code",
            table: "contact_import_rows",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "error_field",
            table: "contact_import_rows",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "status",
            table: "contact_import_rows",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Pending");

        migrationBuilder.AddColumn<long>(
            name: "file_size_bytes",
            table: "contact_import_batches",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "processed_rows",
            table: "contact_import_batches",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "uploaded_by_name",
            table: "contact_import_batches",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "contact_import_exports",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                contact_import_batch_id = table.Column<long>(type: "bigint", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                storage_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                row_count = table.Column<int>(type: "integer", nullable: false),
                requested_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
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
                table.PrimaryKey("pk_contact_import_exports", x => x.id);
                table.ForeignKey(
                    name: "fk_contact_import_exports_contact_import_batches_contact_impor",
                    column: x => x.contact_import_batch_id,
                    principalTable: "contact_import_batches",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "import_jobs",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                contact_import_batch_id = table.Column<long>(type: "bigint", nullable: false),
                contact_import_export_id = table.Column<long>(type: "bigint", nullable: true),
                requested_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                available_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
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
                table.PrimaryKey("pk_import_jobs", x => x.id);
                table.ForeignKey(
                    name: "fk_import_jobs_contact_import_batches_contact_import_batch_id",
                    column: x => x.contact_import_batch_id,
                    principalTable: "contact_import_batches",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_import_jobs_contact_import_exports_contact_import_export_id",
                    column: x => x.contact_import_export_id,
                    principalTable: "contact_import_exports",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_rows_contact_import_batch_id_status",
            table: "contact_import_rows",
            columns: new[] { "contact_import_batch_id", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_exports_contact_import_batch_id_requested_at",
            table: "contact_import_exports",
            columns: new[] { "contact_import_batch_id", "requested_at" });

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_exports_is_deleted",
            table: "contact_import_exports",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_contact_import_exports_tenant_id_id",
            table: "contact_import_exports",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_contact_import_batch_id",
            table: "import_jobs",
            column: "contact_import_batch_id");

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_contact_import_export_id",
            table: "import_jobs",
            column: "contact_import_export_id");

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_is_deleted",
            table: "import_jobs",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_state_available_at",
            table: "import_jobs",
            columns: new[] { "state", "available_at" });

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_tenant_id_id",
            table: "import_jobs",
            columns: new[] { "tenant_id", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "import_jobs");

        migrationBuilder.DropTable(
            name: "contact_import_exports");

        migrationBuilder.DropIndex(
            name: "ix_contact_import_rows_contact_import_batch_id_status",
            table: "contact_import_rows");

        migrationBuilder.DropColumn(
            name: "error_code",
            table: "contact_import_rows");

        migrationBuilder.DropColumn(
            name: "error_field",
            table: "contact_import_rows");

        migrationBuilder.DropColumn(
            name: "status",
            table: "contact_import_rows");

        migrationBuilder.DropColumn(
            name: "file_size_bytes",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "processed_rows",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "uploaded_by_name",
            table: "contact_import_batches");
    }
}
