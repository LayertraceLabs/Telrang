using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations;

/// <inheritdoc />
public partial class PollLooks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LookJson",
            table: "Polls",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "DefaultLookId",
            table: "AspNetUsers",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SavedLooks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                LookJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SavedLooks", x => x.Id);
                table.ForeignKey(
                    name: "FK_SavedLooks_AspNetUsers_OwnerId",
                    column: x => x.OwnerId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SavedLooks_OwnerId",
            table: "SavedLooks",
            column: "OwnerId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SavedLooks");

        migrationBuilder.DropColumn(
            name: "LookJson",
            table: "Polls");

        migrationBuilder.DropColumn(
            name: "DefaultLookId",
            table: "AspNetUsers");
    }
}
