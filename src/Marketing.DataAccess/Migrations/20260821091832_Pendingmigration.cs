using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class Pendingmigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "onboarding_status",
            table: "users",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "onboarding_step_index",
            table: "users",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "onboarding_updated_on",
            table: "users",
            type: "timestamptz",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "onboarding_status",
            table: "users");

        migrationBuilder.DropColumn(
            name: "onboarding_step_index",
            table: "users");

        migrationBuilder.DropColumn(
            name: "onboarding_updated_on",
            table: "users");
    }
}
