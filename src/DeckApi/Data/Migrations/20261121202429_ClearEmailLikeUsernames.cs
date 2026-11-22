using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeckApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClearEmailLikeUsernames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Usernames used to fall back on preferred_username/name, which can be an email; the pseudo is public.
            // Cleared here, filled again from the `pseudo` claim on the next call.
            migrationBuilder.Sql("UPDATE users SET username = NULL WHERE username LIKE '%@%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
