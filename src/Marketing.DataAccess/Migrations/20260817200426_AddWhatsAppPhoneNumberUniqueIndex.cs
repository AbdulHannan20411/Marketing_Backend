using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddWhatsAppPhoneNumberUniqueIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_whatsapp_connections_phone_number_id",
            table: "whatsapp_connections",
            column: "phone_number_id",
            unique: true,
            filter: "is_deleted = false AND status <> 'Disconnected'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_whatsapp_connections_phone_number_id",
            table: "whatsapp_connections");
    }
}
