using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddExportJobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "export_jobs",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                dataset = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                requested_by_user_id = table.Column<long>(type: "bigint", nullable: false),
                query_json = table.Column<string>(type: "jsonb", nullable: false),
                columns = table.Column<List<string>>(type: "text[]", nullable: false),
                total_records = table.Column<int>(type: "integer", nullable: true),
                processed_records = table.Column<int>(type: "integer", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                file_storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                file_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                table.PrimaryKey("pk_export_jobs", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_expires_at",
            table: "export_jobs",
            column: "expires_at",
            filter: "status = 'Completed' and is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_is_deleted",
            table: "export_jobs",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_tenant_id_id",
            table: "export_jobs",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_tenant_id_requested_by_user_id_created_on",
            table: "export_jobs",
            columns: new[] { "tenant_id", "requested_by_user_id", "created_on" },
            descending: new[] { false, false, true });

        migrationBuilder.CreateIndex(
            name: "ix_export_jobs_tenant_id_requested_by_user_id_fingerprint",
            table: "export_jobs",
            columns: new[] { "tenant_id", "requested_by_user_id", "fingerprint" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "export_jobs");
    }
}
