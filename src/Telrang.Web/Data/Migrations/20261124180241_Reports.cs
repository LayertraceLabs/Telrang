using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations;

/// <inheritdoc />
public partial class Reports : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DisabledAt",
            table: "Polls",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "PollReports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PollId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                TextCipher = table.Column<string>(type: "TEXT", nullable: true),
                ContactCipher = table.Column<string>(type: "TEXT", nullable: true),
                State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                HandledAt = table.Column<long>(type: "INTEGER", nullable: true),
                HandledLogId = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PollReports", x => x.Id);
                table.ForeignKey(
                    name: "FK_PollReports_Polls_PollId",
                    column: x => x.PollId,
                    principalTable: "Polls",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PollReports_PollId",
            table: "PollReports",
            column: "PollId");

        migrationBuilder.CreateIndex(
            name: "IX_PollReports_State_PollId",
            table: "PollReports",
            columns: new[] { "State", "PollId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PollReports");

        migrationBuilder.DropColumn(
            name: "DisabledAt",
            table: "Polls");
    }
}
