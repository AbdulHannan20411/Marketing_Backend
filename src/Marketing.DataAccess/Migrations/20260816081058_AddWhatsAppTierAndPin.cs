using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddWhatsAppTierAndPin : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "messaging_tier",
            table: "whatsapp_connections",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Tier250");

        migrationBuilder.AddColumn<string>(
            name: "registration_pin",
            table: "whatsapp_connections",
            type: "character(6)",
            fixedLength: true,
            maxLength: 6,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "messaging_tier",
            table: "whatsapp_connections");

        migrationBuilder.DropColumn(
            name: "registration_pin",
            table: "whatsapp_connections");
    }
}
