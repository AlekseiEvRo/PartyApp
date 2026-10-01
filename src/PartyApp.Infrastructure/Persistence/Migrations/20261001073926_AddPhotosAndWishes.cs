using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartyApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotosAndWishes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "PartyPhotos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PhotoLikes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PhotoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoLikes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoLikes_PartyPhotos_PhotoId",
                        column: x => x.PhotoId,
                        principalTable: "PartyPhotos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PhotoLikes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Wishes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wishes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Wishes_Users_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoLikes_PhotoId_UserId",
                table: "PhotoLikes",
                columns: new[] { "PhotoId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoLikes_UserId",
                table: "PhotoLikes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_CreatedAt",
                table: "Wishes",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Wishes_PlayerId",
                table: "Wishes",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoLikes");

            migrationBuilder.DropTable(
                name: "Wishes");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PartyPhotos");
        }
    }
}
