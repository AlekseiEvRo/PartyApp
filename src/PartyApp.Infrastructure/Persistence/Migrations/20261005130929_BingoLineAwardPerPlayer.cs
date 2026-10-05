using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BingoLineAwardPerPlayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BingoLineAwards_SessionId_LineIndex",
                table: "BingoLineAwards");

            migrationBuilder.CreateIndex(
                name: "IX_BingoLineAwards_SessionId_LineIndex_PlayerId",
                table: "BingoLineAwards",
                columns: new[] { "SessionId", "LineIndex", "PlayerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BingoLineAwards_SessionId_LineIndex_PlayerId",
                table: "BingoLineAwards");

            migrationBuilder.CreateIndex(
                name: "IX_BingoLineAwards_SessionId_LineIndex",
                table: "BingoLineAwards",
                columns: new[] { "SessionId", "LineIndex" },
                unique: true);
        }
    }
}
