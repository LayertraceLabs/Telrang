using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoClose",
                table: "Polls",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Polls whose ClosesAt is the automatic close (no typed closes: date in the stored definition). Plain SQL
            // that SQLite and Postgres both accept (quoted identifiers, TRUE, LIKE); the definition JSON stores the typed
            // date as "closesOn":"yyyy-mm-dd" (older snapshots: "closesAt":"...").
            migrationBuilder.Sql(
                "UPDATE \"Polls\" SET \"AutoClose\" = TRUE WHERE \"ClosesAt\" IS NOT NULL "
                + "AND \"DefinitionJson\" NOT LIKE '%\"closesOn\":\"%' AND \"DefinitionJson\" NOT LIKE '%\"closesAt\":\"%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoClose",
                table: "Polls");
        }
    }
}
