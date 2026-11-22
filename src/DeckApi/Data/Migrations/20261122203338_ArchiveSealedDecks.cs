using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <summary>Sealed decks are always archived (see <c>SealedDecks</c>): existing ones too. Data only, not reverted.</summary>
    public partial class ArchiveSealedDecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE decks SET is_draft = true WHERE format = 'sealed' AND NOT is_draft;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
