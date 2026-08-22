using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class Pendingmigration2 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "data_retained_until",
            table: "tenants",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "deactivated_by_user_id",
            table: "tenants",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "deactivated_on",
            table: "tenants",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "deactivation_details",
            table: "tenants",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "deactivation_reason",
            table: "tenants",
            type: "character varying(24)",
            maxLength: 24,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "data_retained_until",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "deactivated_by_user_id",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "deactivated_on",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "deactivation_details",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "deactivation_reason",
            table: "tenants");
    }
}
