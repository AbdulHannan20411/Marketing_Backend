using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddTemplateWabaId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "waba_id",
            table: "message_templates",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        // Templates already synced from Meta are credited to the account each tenant is connected to
        // now. Without this, every existing template would drop out of the lists and the campaign
        // picker until someone pressed Sync, and scheduled campaigns would stop on "belongs to a
        // previously connected account". A template carried over from an earlier account is credited
        // too - the next sync takes it back off if the connected account does not list it.
        // Local drafts (no Meta id) are left null, which counts as belonging to every account.
        migrationBuilder.Sql(
            """
            UPDATE message_templates AS template
            SET waba_id = connection.waba_id
            FROM whatsapp_connections AS connection
            WHERE connection.tenant_id = template.tenant_id
              AND connection.is_deleted = false
              AND connection.waba_id IS NOT NULL
              AND template.meta_template_id IS NOT NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "waba_id",
            table: "message_templates");
    }
}
