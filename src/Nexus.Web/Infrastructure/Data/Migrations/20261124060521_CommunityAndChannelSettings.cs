using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommunityAndChannelSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "archived_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "archived_by_user_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_archived_by_user_id",
                table: "conversations",
                column: "archived_by_user_id",
                filter: "archived_by_user_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_archived_by_consistent",
                table: "conversations",
                sql: "(archived_at IS NULL) = (archived_by_user_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_channel_archive_invariants",
                table: "conversations",
                sql: "archived_at IS NULL OR (kind = 'Channel' AND is_community_main_feed = false)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_description_length",
                table: "conversations",
                sql: "description IS NULL OR char_length(description) <= 500");

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_users_archived_by_user_id",
                table: "conversations",
                column: "archived_by_user_id",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversations_users_archived_by_user_id",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "ix_conversations_archived_by_user_id",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_archived_by_consistent",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_channel_archive_invariants",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_description_length",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "archived_by_user_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "description",
                table: "conversations");
        }
    }
}
