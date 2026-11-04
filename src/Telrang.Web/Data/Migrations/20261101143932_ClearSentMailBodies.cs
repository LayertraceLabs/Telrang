using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClearSentMailBodies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pentest AV-04: sent and dead mails no longer keep their bodies (live setup and reset links). Its own
            // migration because TextBody only becomes nullable once OutboxBodiesNullable's table rebuild has run.
            migrationBuilder.Sql("UPDATE OutboxMessages SET TextBody = NULL, HtmlBody = NULL WHERE SentAt IS NOT NULL OR DeadAt IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
