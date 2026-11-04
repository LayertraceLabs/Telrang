using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <summary>
    /// Phase 5b voter model, data part: every existing ballot becomes response 1 of its voter key
    /// (VoterIdentity = 'k:' || VoterKey), polls stored with VoterMode "Link" become "Public"
    /// (DefinitionJson snapshots are upgraded on read by PollJson), then the unique
    /// (PollId, VoterIdentity, ResponseNo) index is created. Standard SQL (|| concatenation).
    /// </summary>
    public partial class Phase5bVotersBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Ballots\" SET \"VoterIdentity\" = 'k:' || \"VoterKey\", \"ResponseNo\" = 1 WHERE \"VoterIdentity\" = '';");
            migrationBuilder.Sql("UPDATE \"Polls\" SET \"VoterMode\" = 'Public' WHERE \"VoterMode\" = 'Link';");

            migrationBuilder.DropIndex(
                name: "IX_Ballots_PollId",
                table: "Ballots");

            migrationBuilder.DropIndex(
                name: "IX_Ballots_UserId",
                table: "Ballots");

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_PollId_VoterIdentity_ResponseNo",
                table: "Ballots",
                columns: new[] { "PollId", "VoterIdentity", "ResponseNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_UserId_PollId",
                table: "Ballots",
                columns: new[] { "UserId", "PollId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ballots_PollId_VoterIdentity_ResponseNo",
                table: "Ballots");

            migrationBuilder.DropIndex(
                name: "IX_Ballots_UserId_PollId",
                table: "Ballots");

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_PollId",
                table: "Ballots",
                column: "PollId");

            migrationBuilder.CreateIndex(
                name: "IX_Ballots_UserId",
                table: "Ballots",
                column: "UserId");
        }
    }
}
