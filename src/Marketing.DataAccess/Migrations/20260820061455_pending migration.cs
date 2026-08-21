using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddTemplateListingIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_message_templates_tenant_id_modified_on",
            table: "message_templates",
            columns: new[] { "tenant_id", "modified_on" });

        migrationBuilder.CreateIndex(
            name: "ix_message_templates_tenant_id_status",
            table: "message_templates",
            columns: new[] { "tenant_id", "status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_message_templates_tenant_id_modified_on",
            table: "message_templates");

        migrationBuilder.DropIndex(
            name: "ix_message_templates_tenant_id_status",
            table: "message_templates");
    }
}
