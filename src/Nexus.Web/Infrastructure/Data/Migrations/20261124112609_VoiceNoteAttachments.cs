using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoiceNoteAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "duration_ms",
                table: "attachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_duration_ms",
                table: "attachments",
                sql: "duration_ms IS NULL OR (duration_ms > 0 AND duration_ms <= 120000)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_duration_ms",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "duration_ms",
                table: "attachments");
        }
    }
}
