using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MeetingRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_online_meeting",
                table: "calendar_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "meetings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calendar_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meetings", x => x.id);
                    table.CheckConstraint("ck_meetings_single_source", "(calendar_event_id IS NULL) <> (conversation_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_meetings_calendar_events_calendar_event_id",
                        column: x => x.calendar_event_id,
                        principalTable: "calendar_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_meetings_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_meetings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_meetings_users_host_id",
                        column: x => x.host_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meeting_bans",
                columns: table => new
                {
                    meeting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meeting_bans", x => new { x.meeting_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_meeting_bans_meetings_meeting_id",
                        column: x => x.meeting_id,
                        principalTable: "meetings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_meeting_bans_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meeting_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    meeting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meeting_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_meeting_grants_app_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "app_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_meeting_grants_meetings_meeting_id",
                        column: x => x.meeting_id,
                        principalTable: "meetings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_meeting_grants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_meeting_bans_user_id",
                table: "meeting_bans",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_grants_access_check",
                table: "meeting_grants",
                columns: new[] { "checked_at", "id" },
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_grants_pending_removal",
                table: "meeting_grants",
                columns: new[] { "revoked_at", "id" },
                filter: "revoked_at IS NOT NULL AND provider_removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_meeting_grants_session_id",
                table: "meeting_grants",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_grants_user_meeting",
                table: "meeting_grants",
                columns: new[] { "user_id", "meeting_id" });

            migrationBuilder.CreateIndex(
                name: "ux_meeting_grants_active_session",
                table: "meeting_grants",
                columns: new[] { "meeting_id", "session_id" },
                unique: true,
                filter: "revoked_at IS NULL AND session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_meetings_conversation_id",
                table: "meetings",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "IX_meetings_host_id",
                table: "meetings",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "IX_meetings_organization_id",
                table: "meetings",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_meetings_pending_close",
                table: "meetings",
                columns: new[] { "ended_at", "id" },
                filter: "ended_at IS NOT NULL AND provider_ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_meetings_active_conversation",
                table: "meetings",
                columns: new[] { "conversation_id", "ended_at" },
                unique: true,
                filter: "conversation_id IS NOT NULL AND ended_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_meetings_calendar_event",
                table: "meetings",
                column: "calendar_event_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meeting_bans");

            migrationBuilder.DropTable(
                name: "meeting_grants");

            migrationBuilder.DropTable(
                name: "meetings");

            migrationBuilder.DropColumn(
                name: "is_online_meeting",
                table: "calendar_events");
        }
    }
}
