using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ForwardedChatContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_channel_posts_author_user_id",
                table: "channel_posts");

            migrationBuilder.AddColumn<bool>(
                name: "is_forwarded",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "forward_client_msg_id",
                table: "channel_posts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "forward_payload_sha256",
                table: "channel_posts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_channel_posts_forward_author_client_id",
                table: "channel_posts",
                columns: new[] { "author_user_id", "forward_client_msg_id" },
                unique: true,
                filter: "forward_client_msg_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_object_key",
                table: "attachments",
                column: "object_key");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_thumbnail_object_key",
                table: "attachments",
                column: "thumbnail_object_key",
                filter: "thumbnail_object_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_channel_posts_forward_author_client_id",
                table: "channel_posts");

            migrationBuilder.DropIndex(
                name: "ix_attachments_object_key",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_thumbnail_object_key",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "is_forwarded",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "forward_client_msg_id",
                table: "channel_posts");

            migrationBuilder.DropColumn(
                name: "forward_payload_sha256",
                table: "channel_posts");

            migrationBuilder.CreateIndex(
                name: "IX_channel_posts_author_user_id",
                table: "channel_posts",
                column: "author_user_id");
        }
    }
}
