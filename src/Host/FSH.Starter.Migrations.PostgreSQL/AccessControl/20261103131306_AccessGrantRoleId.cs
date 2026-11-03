using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.AccessControl
{
    /// <summary>
    /// Grants were keyed by role name; re-key them by Identity role Id, so a renamed role keeps its grants and a new
    /// role reusing a deleted role's name does not inherit them. Rows whose name matches no role of their tenant
    /// (left behind by a rename or delete) are dropped: no role holds them any more.
    /// </summary>
    public partial class AccessGrantRoleId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_access_grants_Role_Subject_Action",
                schema: "access",
                table: "access_grants");

            migrationBuilder.AddColumn<string>(
                name: "RoleId",
                schema: "access",
                table: "access_grants",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            // Grants stored Identity's spelling of the name; roles are unique per (NormalizedName, TenantId).
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('identity."Roles"') IS NOT NULL THEN
                        UPDATE access.access_grants g
                        SET "RoleId" = r."Id"
                        FROM identity."Roles" r
                        WHERE r."TenantId" = g."TenantId" AND r."NormalizedName" = upper(g."Role");
                    END IF;
                END $$;
                DELETE FROM access.access_grants WHERE "RoleId" IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "Role",
                schema: "access",
                table: "access_grants");

            migrationBuilder.AlterColumn<string>(
                name: "RoleId",
                schema: "access",
                table: "access_grants",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_access_grants_RoleId_Subject_Action",
                schema: "access",
                table: "access_grants",
                columns: new[] { "RoleId", "Subject", "Action", "TenantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_access_grants_RoleId_Subject_Action",
                schema: "access",
                table: "access_grants");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                schema: "access",
                table: "access_grants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF to_regclass('identity."Roles"') IS NOT NULL THEN
                        UPDATE access.access_grants g
                        SET "Role" = r."Name"
                        FROM identity."Roles" r
                        WHERE r."Id" = g."RoleId";
                    END IF;
                END $$;
                DELETE FROM access.access_grants WHERE "Role" IS NULL;
                """);

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "access",
                table: "access_grants");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                schema: "access",
                table: "access_grants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_access_grants_Role_Subject_Action",
                schema: "access",
                table: "access_grants",
                columns: new[] { "Role", "Subject", "Action", "TenantId" },
                unique: true);
        }
    }
}
