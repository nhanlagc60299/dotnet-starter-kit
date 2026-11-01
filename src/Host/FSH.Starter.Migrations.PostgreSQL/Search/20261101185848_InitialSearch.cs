using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace FSH.Starter.Migrations.PostgreSQL.Search
{
    /// <inheritdoc />
    public partial class InitialSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "search");

            // Hand-written: the generated Tsv column below needs the search.vi configuration to exist first.
            // unaccent is a trusted extension, so a database owner can create it without superuser. search.vi
            // is `simple` with unaccent in front, so "Đà Nẵng" and "da nang" index to the same lexemes. The
            // dictionary is qualified with the schema unaccent actually lives in (it may predate this migration).
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS unaccent;
                CREATE TEXT SEARCH CONFIGURATION search.vi (COPY = simple);
                DO $$
                DECLARE ext_schema text;
                BEGIN
                    SELECT n.nspname INTO ext_schema
                    FROM pg_extension x JOIN pg_namespace n ON n.oid = x.extnamespace
                    WHERE x.extname = 'unaccent';
                    EXECUTE format(
                        'ALTER TEXT SEARCH CONFIGURATION search.vi ALTER MAPPING FOR hword, hword_part, word WITH %I.unaccent, simple',
                        ext_schema);
                END
                $$;
                """);

            migrationBuilder.CreateTable(
                name: "search_documents",
                schema: "search",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('search.vi'::regconfig, coalesce(\"Title\", '')), 'A') || setweight(to_tsvector('search.vi'::regconfig, coalesce(\"Summary\", '')), 'B') || setweight(to_tsvector('search.vi'::regconfig, coalesce(\"Body\", '')), 'C')", stored: true),
                    TenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_documents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_EntityType_EntityId",
                schema: "search",
                table: "search_documents",
                columns: new[] { "EntityType", "EntityId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_search_documents_Tsv",
                schema: "search",
                table: "search_documents",
                column: "Tsv")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_documents",
                schema: "search");

            // The unaccent extension stays: other schemas may use it.
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION IF EXISTS search.vi;");
        }
    }
}
