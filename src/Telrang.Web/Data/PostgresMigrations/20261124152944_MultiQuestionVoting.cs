using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.PostgresMigrations;

/// <inheritdoc />
public partial class MultiQuestionVoting : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // DESIGN.md §34.9: every poll stored before is one question on one page.
        migrationBuilder.AddColumn<int>(
            name: "PageCount",
            table: "Polls",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "QuestionCount",
            table: "Polls",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<string>(
            name: "TextCipher",
            table: "Ballots",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ResponseDrafts",
            columns: table => new
            {
                PollId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<string>(type: "text", maxLength: 450, nullable: false),
                ResponseNo = table.Column<int>(type: "integer", nullable: false),
                Cipher = table.Column<string>(type: "text", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ResponseDrafts", x => new { x.PollId, x.UserId, x.ResponseNo });
                table.ForeignKey(
                    name: "FK_ResponseDrafts_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ResponseDrafts_Polls_PollId",
                    column: x => x.PollId,
                    principalTable: "Polls",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ResponseDrafts_UserId",
            table: "ResponseDrafts",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ResponseDrafts");

        migrationBuilder.DropColumn(
            name: "PageCount",
            table: "Polls");

        migrationBuilder.DropColumn(
            name: "QuestionCount",
            table: "Polls");

        migrationBuilder.DropColumn(
            name: "TextCipher",
            table: "Ballots");
    }
}
