using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddSubscriptionExpiryReminderMarker : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "last_expiry_reminder_day",
            table: "tenant_subscriptions",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "last_expiry_reminder_day",
            table: "tenant_subscriptions");
    }
}
