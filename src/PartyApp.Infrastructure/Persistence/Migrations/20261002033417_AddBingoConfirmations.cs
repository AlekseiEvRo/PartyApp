using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBingoConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BingoCellConfirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CellIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BingoCellConfirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BingoCellConfirmations_EventSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "EventSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BingoCellConfirmations_SessionId_CellIndex",
                table: "BingoCellConfirmations",
                columns: new[] { "SessionId", "CellIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BingoCellConfirmations");
        }
    }
}
