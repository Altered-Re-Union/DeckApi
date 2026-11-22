using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_catalog_snapshots",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_catalog_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "legacy_pending_deletes",
                columns: table => new
                {
                    legacy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legacy_pending_deletes", x => x.legacy_id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    keycloak_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    locale = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "decks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    is_draft = table.Column<bool>(type: "boolean", nullable: false),
                    altered_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    compressed = table.Column<string>(type: "text", nullable: false),
                    extra_cards = table.Column<string>(type: "jsonb", nullable: true),
                    card_references = table.Column<string[]>(type: "text[]", nullable: false),
                    card_families = table.Column<string[]>(type: "text[]", nullable: false),
                    card_count = table.Column<int>(type: "integer", nullable: false),
                    hero_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    hero_faction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    by_rarity = table.Column<string>(type: "jsonb", nullable: false),
                    alt_art_overrides = table.Column<string>(type: "jsonb", nullable: true),
                    legal = table.Column<bool>(type: "boolean", nullable: false),
                    validation_errors = table.Column<string>(type: "jsonb", nullable: true),
                    legality_detail = table.Column<string>(type: "jsonb", nullable: true),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    view_count = table.Column<int>(type: "integer", nullable: false),
                    upvote_count = table.Column<int>(type: "integer", nullable: false),
                    legacy_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_sync_pending = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decks", x => x.id);
                    table.ForeignKey(
                        name: "fk_decks_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deck_upvotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deck_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deck_upvotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_deck_upvotes_decks_deck_id",
                        column: x => x.deck_id,
                        principalTable: "decks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deck_upvotes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deck_upvotes_deck_id_user_id",
                table: "deck_upvotes",
                columns: new[] { "deck_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deck_upvotes_user_id",
                table: "deck_upvotes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_decks_altered_id",
                table: "decks",
                column: "altered_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decks_card_families",
                table: "decks",
                column: "card_families")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_decks_card_references",
                table: "decks",
                column: "card_references")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_decks_format",
                table: "decks",
                column: "format");

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

            migrationBuilder.CreateIndex(
                name: "ix_decks_legacy_id",
                table: "decks",
                column: "legacy_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decks_user_id_updated_at",
                table: "decks",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_legacy_pending_deletes_user_id",
                table: "legacy_pending_deletes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_keycloak_id",
                table: "users",
                column: "keycloak_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_catalog_snapshots");

            migrationBuilder.DropTable(
                name: "deck_upvotes");

            migrationBuilder.DropTable(
                name: "legacy_pending_deletes");

            migrationBuilder.DropTable(
                name: "decks");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
