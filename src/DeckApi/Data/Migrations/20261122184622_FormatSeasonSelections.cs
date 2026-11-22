using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class FormatSeasonSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "format_season_selections",
                columns: table => new
                {
                    format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    season = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    manifest_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manifest_version = table.Column<int>(type: "integer", nullable: true),
                    unique_allowlist = table.Column<string[]>(type: "text[]", nullable: false),
                    first_selected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    selected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_format_season_selections", x => new { x.format, x.season });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "format_season_selections");
        }
    }
}
