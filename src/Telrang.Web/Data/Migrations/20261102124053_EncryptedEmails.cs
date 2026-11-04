using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Telrang.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EncryptedEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_To_CreatedAt",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<string>(
                name: "ToHash",
                table: "OutboxMessages",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ToHash_CreatedAt",
                table: "OutboxMessages",
                columns: new[] { "ToHash", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ToHash_CreatedAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "ToHash",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_To_CreatedAt",
                table: "OutboxMessages",
                columns: new[] { "To", "CreatedAt" });
        }
    }
}
