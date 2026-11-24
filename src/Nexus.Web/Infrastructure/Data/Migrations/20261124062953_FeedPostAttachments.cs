using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeedPostAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "post_id",
                table: "attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_channel_files",
                table: "attachments",
                columns: new[] { "conversation_id", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "state = 'Attached' AND post_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_post_id",
                table: "attachments",
                column: "post_id",
                filter: "post_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_single_parent",
                table: "attachments",
                sql: "NOT (message_id IS NOT NULL AND post_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_attachments_channel_posts_post_id",
                table: "attachments",
                column: "post_id",
                principalTable: "channel_posts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attachments_channel_posts_post_id",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_channel_files",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_post_id",
                table: "attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_single_parent",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "post_id",
                table: "attachments");
        }
    }
}
