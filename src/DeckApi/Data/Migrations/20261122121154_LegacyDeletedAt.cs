using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class LegacyDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "legacy_deleted_at",
                table: "decks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "legacy_deleted_at",
                table: "decks");
        }
    }
}
