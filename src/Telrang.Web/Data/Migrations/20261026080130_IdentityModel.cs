using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <summary>
    /// DESIGN.md §16 identity model: <c>PollGrants</c> (server-side password grants of signed-in accounts) and
    /// <c>InviteTokens.ClaimedByUserId</c>. Data step: an invite token whose ballots were cast while signed in by
    /// exactly one account (the old model recorded <c>UserId</c> on them) is claimed by that account and its
    /// ballots are re-keyed to it (<c>u:</c> + id, <c>VoterKey</c> null), so they open on any browser of that
    /// account. A token is left unclaimed (a bearer link as before) when that account already has other
    /// ballots in the poll, which would collide. Standard SQL (|| concatenation); no table rebuild, so
    /// the whole migration runs in one transaction. Down reverses the re-keying.
    /// </summary>
    public partial class IdentityModel : Migration
    {
        private const string TokenKey = "'invite:' || \"InviteTokens\".\"TokenHash\"";

        // The one account among the token's ballots (COUNT(DISTINCT) ignores NULL, anonymous revisions).
        private const string TokenUser =
            "(SELECT MIN(b.\"UserId\") FROM \"Ballots\" b WHERE b.\"PollId\" = \"InviteTokens\".\"PollId\" AND b.\"VoterKey\" = " + TokenKey + ")";

        private const string ClaimSql =
            "UPDATE \"InviteTokens\" SET \"ClaimedByUserId\" = " + TokenUser +
            " WHERE (SELECT COUNT(DISTINCT b.\"UserId\") FROM \"Ballots\" b WHERE b.\"PollId\" = \"InviteTokens\".\"PollId\" AND b.\"VoterKey\" = " + TokenKey + ") = 1" +
            " AND NOT EXISTS (SELECT 1 FROM \"Ballots\" o WHERE o.\"PollId\" = \"InviteTokens\".\"PollId\" AND o.\"UserId\" = " + TokenUser +
            " AND (o.\"VoterKey\" IS NULL OR o.\"VoterKey\" <> " + TokenKey + "));";

        private const string ClaimOfBallot =
            "(SELECT t.\"ClaimedByUserId\" FROM \"InviteTokens\" t WHERE t.\"PollId\" = \"Ballots\".\"PollId\" AND 'invite:' || t.\"TokenHash\" = \"Ballots\".\"VoterKey\")";

        // All SET expressions read the old row, so VoterKey is still the token key in the subqueries.
        private const string RekeySql =
            "UPDATE \"Ballots\" SET \"UserId\" = " + ClaimOfBallot + ", \"VoterIdentity\" = 'u:' || " + ClaimOfBallot + ", \"VoterKey\" = NULL" +
            " WHERE EXISTS (SELECT 1 FROM \"InviteTokens\" t WHERE t.\"PollId\" = \"Ballots\".\"PollId\" AND 'invite:' || t.\"TokenHash\" = \"Ballots\".\"VoterKey\"" +
            " AND t.\"ClaimedByUserId\" IS NOT NULL);";

        private const string TokenOfAccountBallot =
            "(SELECT t.\"TokenHash\" FROM \"InviteTokens\" t WHERE t.\"PollId\" = \"Ballots\".\"PollId\" AND t.\"ClaimedByUserId\" = \"Ballots\".\"UserId\")";

        private const string UnkeySql =
            "UPDATE \"Ballots\" SET \"VoterKey\" = 'invite:' || " + TokenOfAccountBallot + ", \"VoterIdentity\" = 'k:invite:' || " + TokenOfAccountBallot +
            " WHERE \"VoterKey\" IS NULL AND EXISTS (SELECT 1 FROM \"InviteTokens\" t WHERE t.\"PollId\" = \"Ballots\".\"PollId\"" +
            " AND t.\"ClaimedByUserId\" = \"Ballots\".\"UserId\");";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedByUserId",
                table: "InviteTokens",
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.Sql(ClaimSql);
            migrationBuilder.Sql(RekeySql);

            migrationBuilder.CreateTable(
                name: "PollGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PollId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                name: "IX_InviteTokens_PollId_ClaimedByUserId",
                table: "InviteTokens",
                columns: new[] { "PollId", "ClaimedByUserId" },
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UnkeySql);

            migrationBuilder.DropTable(
                name: "PollGrants");

            migrationBuilder.DropIndex(
                name: "IX_InviteTokens_PollId_ClaimedByUserId",
                table: "InviteTokens");

            migrationBuilder.DropColumn(
                name: "ClaimedByUserId",
                table: "InviteTokens");
        }
    }
}
