using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatMessageBookmarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_pins",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_pins", x => x.message_id);
                    table.ForeignKey(
                        name: "FK_message_pins_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_message_pins_users_pinned_by_user_id",
                        column: x => x.pinned_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "saved_messages",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_saved = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_messages", x => new { x.user_id, x.message_id });
                    table.ForeignKey(
                        name: "FK_saved_messages_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_saved_messages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_pins_pinned_by_user_id",
                table: "message_pins",
                column: "pinned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_messages_message_id",
                table: "saved_messages",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_messages_user_cursor",
                table: "saved_messages",
                columns: new[] { "user_id", "saved_at", "message_id" },
                filter: "is_saved");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_pins");

            migrationBuilder.DropTable(
                name: "saved_messages");
        }
    }
}
