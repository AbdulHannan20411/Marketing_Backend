using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class AddNotificationPreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "user_notification_preferences",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
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
                table.PrimaryKey("pk_user_notification_preferences", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_user_notification_preferences_is_deleted",
            table: "user_notification_preferences",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_notification_preferences_user_id_category",
            table: "user_notification_preferences",
            columns: new[] { "user_id", "category" },
            unique: true,
            filter: "is_deleted = false");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "user_notification_preferences");
    }
}
