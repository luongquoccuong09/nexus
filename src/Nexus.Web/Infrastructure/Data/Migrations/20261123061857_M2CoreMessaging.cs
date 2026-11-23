using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2CoreMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    dm_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    next_seq = table.Column<long>(type: "bigint", nullable: false),
                    change_seq = table.Column<long>(type: "bigint", nullable: false),
                    min_available_change_seq = table.Column<long>(type: "bigint", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.id);
                    table.ForeignKey(
                        name: "FK_conversations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conversations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_blocks",
                columns: table => new
                {
                    blocker_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_blocks", x => new { x.blocker_user_id, x.blocked_user_id });
                    table.ForeignKey(
                        name: "FK_user_blocks_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_blocks_users_blocked_user_id",
                        column: x => x.blocked_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_blocks_users_blocker_user_id",
                        column: x => x.blocker_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reported_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_reports", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_reports_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_reports_users_reported_user_id",
                        column: x => x.reported_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_reports_users_reporter_user_id",
                        column: x => x.reporter_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conversation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_seq = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_conversation_events_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_members",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_read_seq = table.Column<long>(type: "bigint", nullable: false),
                    visible_from_seq = table.Column<long>(type: "bigint", nullable: false),
                    muted = table.Column<bool>(type: "boolean", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_members", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_conversation_members_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_conversation_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_msg_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_messages_users_sender_id",
                        column: x => x.sender_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_events_created_at",
                table: "conversation_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ux_conversation_events_conv_change_seq",
                table: "conversation_events",
                columns: new[] { "conversation_id", "change_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversation_members_user_active",
                table: "conversation_members",
                columns: new[] { "user_id", "left_at" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_created_by_user_id",
                table: "conversations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_org_updated",
                table: "conversations",
                columns: new[] { "organization_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_conversations_org_dm_key",
                table: "conversations",
                columns: new[] { "organization_id", "dm_key" },
                unique: true,
                filter: "dm_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_messages_conversation_seq",
                table: "messages",
                columns: new[] { "conversation_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_messages_sender_client_msg_id",
                table: "messages",
                columns: new[] { "sender_id", "client_msg_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_blocks_blocked_user_id",
                table: "user_blocks",
                column: "blocked_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_blocks_organization_id",
                table: "user_blocks",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_reports_created_at",
                table: "user_reports",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_user_reports_organization_id",
                table: "user_reports",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_reports_reported_user_id",
                table: "user_reports",
                column: "reported_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_reports_reporter_user_id",
                table: "user_reports",
                column: "reporter_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_events");

            migrationBuilder.DropTable(
                name: "conversation_members");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "user_blocks");

            migrationBuilder.DropTable(
                name: "user_reports");

            migrationBuilder.DropTable(
                name: "conversations");
        }
    }
}
