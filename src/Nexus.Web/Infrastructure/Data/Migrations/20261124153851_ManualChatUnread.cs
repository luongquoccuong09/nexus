using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualChatUnread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "manual_unread_from_seq",
                table: "conversation_members",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "manual_unread_version",
                table: "conversation_members",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "manual_unread_from_seq",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "manual_unread_version",
                table: "conversation_members");
        }
    }
}
