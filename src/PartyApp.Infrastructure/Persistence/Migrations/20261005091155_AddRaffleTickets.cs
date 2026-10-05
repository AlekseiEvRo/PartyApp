using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRaffleTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WinnerTicketNumber",
                table: "RaffleDraws",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TicketNumber",
                table: "PlayerSubmissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSubmissions_SessionId_TicketNumber",
                table: "PlayerSubmissions",
                columns: new[] { "SessionId", "TicketNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayerSubmissions_SessionId_TicketNumber",
                table: "PlayerSubmissions");

            migrationBuilder.DropColumn(
                name: "WinnerTicketNumber",
                table: "RaffleDraws");

            migrationBuilder.DropColumn(
                name: "TicketNumber",
                table: "PlayerSubmissions");
        }
    }
}
