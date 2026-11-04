using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePollPasswordsAddAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PollGrants");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Polls");

            migrationBuilder.DropColumn(
                name: "PasswordVersion",
                table: "Polls");

            migrationBuilder.RenameColumn(
                name: "Label",
                table: "InviteTokens",
                newName: "Alias");

            migrationBuilder.AddColumn<string>(
                name: "Alias",
                table: "Ballots",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Alias",
                table: "Ballots");

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Polls",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PasswordVersion",
                table: "Polls",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.RenameColumn(
                name: "Alias",
                table: "InviteTokens",
                newName: "Label");

            migrationBuilder.CreateTable(
                name: "PollGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PollId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PasswordVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PollGrants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PollGrants_Polls_PollId",
                        column: x => x.PollId,
                        principalTable: "Polls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PollGrants_PollId_UserId",
                table: "PollGrants",
                columns: new[] { "PollId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PollGrants_UserId",
                table: "PollGrants",
                column: "UserId");
        }
    }
}
