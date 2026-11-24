using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatMessageThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_reply_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_reply_seq",
                table: "messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_message_id",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reply_count",
                table: "messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "latest_main_seq",
                table: "conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Every message created before this migration belongs to the main timeline.
            migrationBuilder.Sql("UPDATE conversations SET latest_main_seq = next_seq WHERE next_seq > 0");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_messages_conversation_id_id",
                table: "messages",
                columns: new[] { "conversation_id", "id" });

            migrationBuilder.CreateTable(
                name: "thread_subscriptions",
                columns: table => new
                {
                    parent_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_following = table.Column<bool>(type: "boolean", nullable: false),
                    last_read_reply_seq = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_subscriptions", x => new { x.parent_message_id, x.user_id });
                    table.CheckConstraint("ck_thread_subscriptions_cursor", "last_read_reply_seq >= 0 AND version >= 0");
                    table.ForeignKey(
                        name: "FK_thread_subscriptions_messages_parent_message_id",
                        column: x => x.parent_message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_thread_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_parent_seq",
                table: "messages",
                columns: new[] { "conversation_id", "parent_message_id", "seq" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_thread_metadata",
                table: "messages",
                sql: "(parent_message_id IS NULL OR (parent_message_id <> id AND reply_count = 0)) AND ((reply_count = 0 AND last_reply_at IS NULL AND last_reply_seq IS NULL) OR (reply_count > 0 AND last_reply_at IS NOT NULL AND last_reply_seq IS NOT NULL AND last_reply_seq > seq))");

            migrationBuilder.CreateIndex(
                name: "ix_thread_subscriptions_user_id",
                table: "thread_subscriptions",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_messages_messages_conversation_id_parent_message_id",
                table: "messages",
                columns: new[] { "conversation_id", "parent_message_id" },
                principalTable: "messages",
                principalColumns: new[] { "conversation_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_messages_messages_conversation_id_parent_message_id",
                table: "messages");

            migrationBuilder.DropTable(
                name: "thread_subscriptions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_messages_conversation_id_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_conversation_parent_seq",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_thread_metadata",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "last_reply_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "last_reply_seq",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "parent_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "reply_count",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "latest_main_seq",
                table: "conversations");
        }
    }
}
