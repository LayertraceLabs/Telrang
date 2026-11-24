using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.PostgresMigrations;

/// <inheritdoc />
public partial class Reports : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DisabledAt",
            table: "Polls",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "PollReports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PollId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                TextCipher = table.Column<string>(type: "text", nullable: true),
                ContactCipher = table.Column<string>(type: "text", nullable: true),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                HandledAt = table.Column<long>(type: "bigint", nullable: true),
                HandledLogId = table.Column<long>(type: "bigint", nullable: true)
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
