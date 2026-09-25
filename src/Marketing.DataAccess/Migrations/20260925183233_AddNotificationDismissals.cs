using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddNotificationDismissals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "notification_dismissals",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                notification_id = table.Column<long>(type: "bigint", nullable: false),
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
                table.PrimaryKey("pk_notification_dismissals", x => x.id);
                table.ForeignKey(
                    name: "fk_notification_dismissals_notifications_notification_id",
                    column: x => x.notification_id,
                    principalTable: "notifications",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_notification_dismissals_is_deleted",
            table: "notification_dismissals",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_notification_dismissals_notification_id",
            table: "notification_dismissals",
            column: "notification_id");

        migrationBuilder.CreateIndex(
            name: "ix_notification_dismissals_user_id_notification_id",
            table: "notification_dismissals",
            columns: new[] { "user_id", "notification_id" },
            unique: true,
            filter: "is_deleted = false");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "notification_dismissals");
    }
}
