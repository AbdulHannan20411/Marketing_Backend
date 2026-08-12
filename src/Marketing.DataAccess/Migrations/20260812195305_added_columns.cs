using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddedImportColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "column_mapping",
            table: "contact_import_batches",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "duplicate_strategy",
            table: "contact_import_batches",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "duplicates_in_file",
            table: "contact_import_batches",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "error_report_storage_key",
            table: "contact_import_batches",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "failure_reason",
            table: "contact_import_batches",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "storage_key",
            table: "contact_import_batches",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "uploaded_by_user_id",
            table: "contact_import_batches",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "column_mapping",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "duplicate_strategy",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "duplicates_in_file",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "error_report_storage_key",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "failure_reason",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "storage_key",
            table: "contact_import_batches");

        migrationBuilder.DropColumn(
            name: "uploaded_by_user_id",
            table: "contact_import_batches");
    }
}
