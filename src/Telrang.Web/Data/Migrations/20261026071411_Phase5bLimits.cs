using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase5bLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LimitActivePolls",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LimitBallotsPerPoll",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LimitInvitesPerPoll",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LimitPollDurationDays",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LimitPollsPerDay",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LimitResponsesPerVoter",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SelfRegistered",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LimitActivePolls",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LimitBallotsPerPoll",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LimitInvitesPerPoll",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LimitPollDurationDays",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LimitPollsPerDay",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LimitResponsesPerVoter",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SelfRegistered",
                table: "AspNetUsers");
        }
    }
}
