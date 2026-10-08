using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpyFall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpyFallParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsSpy = table.Column<bool>(type: "INTEGER", nullable: false),
                    VotedForId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VotedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpyFallParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpyFallParticipants_EventSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "EventSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpyFallParticipants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpyFallRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpyUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CitizenWord = table.Column<string>(type: "TEXT", nullable: false),
                    SpyWord = table.Column<string>(type: "TEXT", nullable: false),
                    GuessWord = table.Column<string>(type: "TEXT", nullable: true),
                    GuessCorrect = table.Column<bool>(type: "INTEGER", nullable: false),
                    GuessUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Winner = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpyFallRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpyFallRounds_EventSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "EventSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpyFallRounds_Users_SpyUserId",
                        column: x => x.SpyUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpyFallParticipants_SessionId_UserId",
                table: "SpyFallParticipants",
                columns: new[] { "SessionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpyFallParticipants_UserId",
                table: "SpyFallParticipants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SpyFallRounds_SessionId",
                table: "SpyFallRounds",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpyFallRounds_SpyUserId",
                table: "SpyFallRounds",
                column: "SpyUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpyFallParticipants");

            migrationBuilder.DropTable(
                name: "SpyFallRounds");
        }
    }
}
