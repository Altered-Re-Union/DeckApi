using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeckValidSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "valid_seasons",
                table: "decks",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateIndex(
                name: "ix_decks_valid_seasons",
                table: "decks",
                column: "valid_seasons")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_decks_valid_seasons",
                table: "decks");

            migrationBuilder.DropColumn(
                name: "valid_seasons",
                table: "decks");
        }
    }
}
