using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class DraftsAreValidated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_decks_is_public_is_draft_created_at",
                table: "decks");

            migrationBuilder.DropIndex(
                name: "ix_decks_is_public_is_draft_upvote_count",
                table: "decks");

            migrationBuilder.DropIndex(
                name: "ix_decks_is_public_is_draft_view_count",
                table: "decks");

            migrationBuilder.CreateIndex(
                name: "ix_decks_is_public_legal_created_at",
                table: "decks",
                columns: new[] { "is_public", "legal", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decks_is_public_legal_upvote_count",
                table: "decks",
                columns: new[] { "is_public", "legal", "upvote_count" });

            migrationBuilder.CreateIndex(
                name: "ix_decks_is_public_legal_view_count",
                table: "decks",
                columns: new[] { "is_public", "legal", "view_count" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_decks_is_public_legal_created_at",
                table: "decks");

            migrationBuilder.DropIndex(
                name: "ix_decks_is_public_legal_upvote_count",
                table: "decks");

            migrationBuilder.DropIndex(
                name: "ix_decks_is_public_legal_view_count",
                table: "decks");

            migrationBuilder.CreateIndex(
                name: "ix_decks_is_public_is_draft_created_at",
                table: "decks",
                columns: new[] { "is_public", "is_draft", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decks_is_public_is_draft_upvote_count",
                table: "decks",
                columns: new[] { "is_public", "is_draft", "upvote_count" });

            migrationBuilder.CreateIndex(
                name: "ix_decks_is_public_is_draft_view_count",
                table: "decks",
                columns: new[] { "is_public", "is_draft", "view_count" });
        }
    }
}
