using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedLinksDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shared_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_seq = table.Column<long>(type: "bigint", nullable: true),
                    post_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sender_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_links", x => x.id);
                    table.CheckConstraint("ck_shared_links_lengths", "char_length(url) <= 2048 AND char_length(host) <= 255 AND (label IS NULL OR char_length(label) <= 160)");
                    table.CheckConstraint("ck_shared_links_safe_scheme", "url ~* '^https?://'");
                    table.CheckConstraint("ck_shared_links_single_parent", "(message_id IS NOT NULL AND post_id IS NULL AND message_seq IS NOT NULL) OR (post_id IS NOT NULL AND message_id IS NULL AND message_seq IS NULL)");
                    table.ForeignKey(
                        name: "FK_shared_links_channel_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "channel_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_shared_links_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_shared_links_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_shared_links_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_links_users_sender_user_id",
                        column: x => x.sender_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shared_links_conv_created",
                table: "shared_links",
                columns: new[] { "conversation_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_shared_links_message_id",
                table: "shared_links",
                column: "message_id",
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_links_organization_id",
                table: "shared_links",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_shared_links_post_id",
                table: "shared_links",
                column: "post_id",
                filter: "post_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_links_sender_user_id",
                table: "shared_links",
                column: "sender_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shared_links");
        }
    }
}
