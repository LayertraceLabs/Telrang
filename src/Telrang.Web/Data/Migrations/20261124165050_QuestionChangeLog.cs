using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations;

/// <inheritdoc />
public partial class QuestionChangeLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "QuestionKey",
            table: "PollChanges",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "QuestionLabel",
            table: "PollChanges",
            type: "TEXT",
            maxLength: 200,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "QuestionKey",
            table: "PollChanges");

        migrationBuilder.DropColumn(
            name: "QuestionLabel",
            table: "PollChanges");
    }
}
