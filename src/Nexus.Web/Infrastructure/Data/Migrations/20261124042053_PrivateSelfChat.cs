using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrivateSelfChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_kind",
                table: "conversations",
                sql: "kind IN ('Dm', 'Group', 'Channel', 'Self')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_self_invariants",
                table: "conversations",
                sql: "kind <> 'Self' OR (organization_id IS NULL AND community_id IS NULL AND dm_key = 'self:' || replace(created_by_user_id::text, '-', ''))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_kind",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_self_invariants",
                table: "conversations");
        }
    }
}
