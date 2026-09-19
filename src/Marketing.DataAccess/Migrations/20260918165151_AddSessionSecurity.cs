using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddSessionSecurity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "security_events",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: true),
                kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                location = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                device_label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
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
                table.PrimaryKey("pk_security_events", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "user_sessions",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                device_label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                browser = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                operating_system = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                device_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                last_ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                location = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                last_activity_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                revoked_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                revoked_by_user_id = table.Column<long>(type: "bigint", nullable: true),
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
                table.PrimaryKey("pk_user_sessions", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_session_id",
            table: "refresh_tokens",
            column: "session_id");

        migrationBuilder.CreateIndex(
            name: "ix_security_events_is_deleted",
            table: "security_events",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_security_events_tenant_id_id",
            table: "security_events",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_security_events_user_id_kind_occurred_at",
            table: "security_events",
            columns: new[] { "user_id", "kind", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_is_deleted",
            table: "user_sessions",
            column: "is_deleted",
            filter: "is_deleted = false");

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_session_id",
            table: "user_sessions",
            column: "session_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_tenant_id_id",
            table: "user_sessions",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_tenant_id_last_activity_at",
            table: "user_sessions",
            columns: new[] { "tenant_id", "last_activity_at" });

        migrationBuilder.CreateIndex(
            name: "ix_user_sessions_user_id_last_activity_at",
            table: "user_sessions",
            columns: new[] { "user_id", "last_activity_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "security_events");

        migrationBuilder.DropTable(
            name: "user_sessions");

        migrationBuilder.DropIndex(
            name: "ix_refresh_tokens_session_id",
            table: "refresh_tokens");
    }
}
