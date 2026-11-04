using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <summary>
    /// TODO sweep (DESIGN.md §13, 2026-10-26): <c>Ballots.NotifyOnClose</c> and <c>Polls.ClosedNotifiedAt</c> (results-ready
    /// mail), the <c>PollCreations</c> table (the polls-per-day limit counts creations; it starts empty, so creations
    /// before the upgrade do not count against the first day), and an auto-close backfill.
    /// <para>
    /// Backfill: polls created before auto-close existed have no stored close (<c>ClosesAt</c> null). They get
    /// <c>CreatedAt</c> + the owner's poll duration (the account override, else the stored
    /// <c>Limits.MaxPollDurationDays</c> setting, else the default 180 days); 0 (no limit) leaves them open-ended.
    /// <b>This closes old open-ended polls on upgrade</b> when they are older than that duration; a later
    /// <c>closes:</c> date in the editor reopens one. The date arithmetic is the only provider-specific SQL (SQLite's
    /// strftime, writing the same text format EF uses for DateTimeOffset); another provider needs its own here.
    /// </para>
    /// </summary>
    public partial class TodoSweep : Migration
    {
        private const string DurationDays =
            "COALESCE((SELECT u.\"LimitPollDurationDays\" FROM \"AspNetUsers\" u WHERE u.\"Id\" = \"Polls\".\"OwnerId\")," +
            " (SELECT CAST(s.\"Value\" AS INTEGER) FROM \"AppSettings\" s WHERE s.\"Key\" = 'Limits.MaxPollDurationDays'), 180)";

        private const string SqliteBackfill =
            "UPDATE \"Polls\" SET \"ClosesAt\" = strftime('%Y-%m-%d %H:%M:%f', \"CreatedAt\", '+' || " + DurationDays + " || ' days') || '+00:00'" +
            " WHERE \"ClosesAt\" IS NULL AND " + DurationDays + " > 0;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                throw new NotSupportedException("TodoSweep: the auto-close backfill is written for SQLite only.");
            migrationBuilder.Sql(SqliteBackfill);

            migrationBuilder.AddColumn<long>(
                name: "ClosedNotifiedAt",
                table: "Polls",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnClose",
                table: "Ballots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PollCreations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollCreations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PollCreations_At",
                table: "PollCreations",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_PollCreations_UserId_At",
                table: "PollCreations",
                columns: new[] { "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PollCreations");

            migrationBuilder.DropColumn(
                name: "ClosedNotifiedAt",
                table: "Polls");

            migrationBuilder.DropColumn(
                name: "NotifyOnClose",
                table: "Ballots");
        }
    }
}
