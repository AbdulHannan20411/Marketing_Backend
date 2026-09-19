using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketing.DataAccess.Migrations;
/// <inheritdoc />
public partial class AddAutoReplyPermissionAndSearchRadius : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "max_search_radius_km",
            table: "subscription_plans",
            type: "integer",
            nullable: true);

        // Existing plans get the radius the pricing sheet gives them; any other plan starts at
        // 10 km, the platform's ceiling. Enterprise stays null: no plan ceiling beyond that 10 km.
        migrationBuilder.Sql(
            """
            UPDATE subscription_plans
            SET max_search_radius_km = CASE lower(trim(name))
                WHEN 'starter' THEN 5
                WHEN 'growth' THEN 10
                WHEN 'scale' THEN 10
                WHEN 'enterprise' THEN NULL
                ELSE 10
            END;
            """);

        // Auto-reply moves from settings.integrations to its own permission. Everyone who could
        // manage it before keeps that ability: an employee granted settings.integrations, and any
        // saved permission set carrying it. Administrators hold it by role and need nothing here.
        migrationBuilder.Sql(
            """
            INSERT INTO user_permission_overrides
                (tenant_id, user_id, permission, is_granted, created_by, created_on, is_deleted)
            SELECT o.tenant_id, o.user_id, 'ai.autoreply.manage', true, 1, now(), false
            FROM user_permission_overrides AS o
            WHERE o.permission = 'settings.integrations'
              AND o.is_granted
              AND NOT o.is_deleted
              AND NOT EXISTS (
                  SELECT 1 FROM user_permission_overrides AS x
                  WHERE x.user_id = o.user_id
                    AND x.permission = 'ai.autoreply.manage'
                    AND NOT x.is_deleted);

            UPDATE permission_sets
            SET permissions = array_append(permissions, 'ai.autoreply.manage')
            WHERE 'settings.integrations' = ANY(permissions)
              AND NOT ('ai.autoreply.manage' = ANY(permissions));
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "max_search_radius_km",
            table: "subscription_plans");
    }
}
