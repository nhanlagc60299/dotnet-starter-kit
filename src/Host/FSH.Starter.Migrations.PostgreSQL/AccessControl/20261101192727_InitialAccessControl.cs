using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.AccessControl
{
    /// <inheritdoc />
    public partial class InitialAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "access_grants",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Condition = table.Column<string>(type: "jsonb", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_grants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_grants_Role_Subject_Action",
                schema: "access",
                table: "access_grants",
                columns: new[] { "Role", "Subject", "Action", "TenantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_grants",
                schema: "access");
        }
    }
}
