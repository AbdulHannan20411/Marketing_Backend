using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;

/// <inheritdoc />
public partial class WorkspaceBillingProfileAndPaymentMethods : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "billing_profiles",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                address_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                address_line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                postal_code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                tax_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                billing_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
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
                table.PrimaryKey("pk_billing_profiles", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "payment_methods",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                provider_token = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                brand = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                expiry_month = table.Column<int>(type: "integer", nullable: true),
                expiry_year = table.Column<int>(type: "integer", nullable: true),
                is_default = table.Column<bool>(type: "boolean", nullable: false),
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
                table.PrimaryKey("pk_payment_methods", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_billing_profiles_is_deleted",
            table: "billing_profiles",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_billing_profiles_tenant_id",
            table: "billing_profiles",
            column: "tenant_id",
            unique: true,
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_billing_profiles_tenant_id_id",
            table: "billing_profiles",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_payment_methods_is_deleted",
            table: "payment_methods",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_payment_methods_tenant_id",
            table: "payment_methods",
            column: "tenant_id",
            unique: true,
            filter: "is_default = true AND is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_payment_methods_tenant_id_id",
            table: "payment_methods",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_payment_methods_tenant_id_provider_token",
            table: "payment_methods",
            columns: new[] { "tenant_id", "provider_token" },
            unique: true,
            filter: "is_deleted = false");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "billing_profiles");

        migrationBuilder.DropTable(
            name: "payment_methods");
    }
}
