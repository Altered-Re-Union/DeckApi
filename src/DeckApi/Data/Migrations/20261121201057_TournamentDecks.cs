using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class TournamentDecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "source",
                table: "decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "deck_tournament_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deck_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tournament_parent_id = table.Column<long>(type: "bigint", nullable: false),
                    tournament_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    bga_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    games = table.Column<int>(type: "integer", nullable: false),
                    wins = table.Column<int>(type: "integer", nullable: false),
                    losses = table.Column<int>(type: "integer", nullable: false),
                    last_played_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deck_tournament_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_deck_tournament_results_decks_deck_id",
                        column: x => x.deck_id,
                        principalTable: "decks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_cursors",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_cursors", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_decks_source_is_public_created_at",
                table: "decks",
                columns: new[] { "source", "is_public", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decks_user_id_source",
                table: "decks",
                columns: new[] { "user_id", "source" });

            migrationBuilder.CreateIndex(
                name: "ix_deck_tournament_results_deck_id",
                table: "deck_tournament_results",
                column: "deck_id");

            migrationBuilder.CreateIndex(
                name: "ix_deck_tournament_results_tournament_parent_id_bga_user_id",
                table: "deck_tournament_results",
                columns: new[] { "tournament_parent_id", "bga_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deck_tournament_results");

            migrationBuilder.DropTable(
                name: "sync_cursors");

            migrationBuilder.DropIndex(
                name: "ix_decks_source_is_public_created_at",
                table: "decks");

            migrationBuilder.DropIndex(
                name: "ix_decks_user_id_source",
                table: "decks");

            migrationBuilder.DropColumn(
                name: "source",
                table: "decks");
        }
    }
}
