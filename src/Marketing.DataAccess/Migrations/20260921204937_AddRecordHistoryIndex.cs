using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddRecordHistoryIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_audit_logs_entity_name_entity_id",
            table: "audit_logs");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_entity_name_entity_id_occurred_on",
            table: "audit_logs",
            columns: new[] { "entity_name", "entity_id", "occurred_on" },
            descending: new[] { false, false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_audit_logs_entity_name_entity_id_occurred_on",
            table: "audit_logs");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_entity_name_entity_id",
            table: "audit_logs",
            columns: new[] { "entity_name", "entity_id" });
    }
}
