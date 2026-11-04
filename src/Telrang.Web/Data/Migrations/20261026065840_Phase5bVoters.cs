using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <summary>
    /// Phase 5b voter model, schema part: ballots get UserId (FK, SET NULL), VoterIdentity ("u:" + user id
    /// or "k:" + voter key) and ResponseNo; VoterKey becomes nullable; the unique (PollId, VoterKey) index
    /// goes. <see cref="Phase5bVotersBackfill"/> fills the new columns and adds the unique
    /// (PollId, VoterIdentity, ResponseNo) index. Two migrations because SQLite rebuilds the Ballots
    /// table here, and data SQL must not run while a rebuild is pending.
    /// </summary>
    public partial class Phase5bVoters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ballots_PollId_VoterKey",
                table: "Ballots");

            migrationBuilder.AlterColumn<string>(
                name: "VoterKey",
                table: "Ballots",
                type: "TEXT",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<int>(
                name: "ResponseNo",
                table: "Ballots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Ballots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoterIdentity",
                table: "Ballots",
                type: "TEXT",
                maxLength: 110,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_PollId",
                table: "Ballots",
                column: "PollId");

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_UserId",
                table: "Ballots",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Ballots_AspNetUsers_UserId",
                table: "Ballots",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old schema allows one ballot per (poll, voter key): keep response 1 and key account
            // ballots by their identity; "Accounts" did not exist, "Public" was "Link".
            migrationBuilder.Sql("DELETE FROM \"Ballots\" WHERE \"ResponseNo\" > 1;");
            migrationBuilder.Sql("UPDATE \"Ballots\" SET \"VoterKey\" = \"VoterIdentity\" WHERE \"VoterKey\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Polls\" SET \"VoterMode\" = 'Link' WHERE \"VoterMode\" IN ('Public', 'Accounts');");

            migrationBuilder.DropForeignKey(
                name: "FK_Ballots_AspNetUsers_UserId",
                table: "Ballots");

            migrationBuilder.DropIndex(
                name: "IX_Ballots_PollId",
                table: "Ballots");

            migrationBuilder.DropIndex(
                name: "IX_Ballots_UserId",
                table: "Ballots");

            migrationBuilder.DropColumn(
                name: "ResponseNo",
                table: "Ballots");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Ballots");

            migrationBuilder.DropColumn(
                name: "VoterIdentity",
                table: "Ballots");

            migrationBuilder.AlterColumn<string>(
                name: "VoterKey",
                table: "Ballots",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_PollId_VoterKey",
                table: "Ballots",
                columns: new[] { "PollId", "VoterKey" },
                unique: true);
        }
    }
}
