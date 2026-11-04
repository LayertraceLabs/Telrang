using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResultsSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DESIGN.md §25: results for anyone with the link only on public polls. Invite and account polls stored with
            // "Open" become "AfterVote" (voters), in the definition and its denormalised column. PollJson reads them the
            // same way, so a definition this misses (not valid JSON) still never shows results to its address alone.
            migrationBuilder.Sql("""
                UPDATE "Polls" SET "DefinitionJson" = json_set("DefinitionJson", '$.results', 'AfterVote')
                WHERE json_valid("DefinitionJson") AND json_extract("DefinitionJson", '$.results') = 'Open'
                  AND json_extract("DefinitionJson", '$.voters') IN ('Invite', 'Accounts');
                """);
            migrationBuilder.Sql("""
                UPDATE "Polls" SET "ResultsVisibility" = 'AfterVote'
                WHERE "ResultsVisibility" = 'Open' AND "VoterMode" IN ('Invite', 'Accounts');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
