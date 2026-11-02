using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.Transfer
{
    /// <inheritdoc />
    public partial class InitialTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "transfer");

            migrationBuilder.CreateTable(
                name: "transfer_jobs",
                schema: "transfer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntityTypes = table.Column<string>(type: "jsonb", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Summary = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_jobs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transfer_jobs",
                schema: "transfer");
        }
    }
}
