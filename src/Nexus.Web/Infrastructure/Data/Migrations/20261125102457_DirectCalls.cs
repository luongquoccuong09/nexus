using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DirectCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_meetings_single_source",
                table: "meetings");

            migrationBuilder.AddColumn<Guid>(
                name: "direct_call_id",
                table: "meetings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "direct_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_call_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    callee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caller_session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    callee_session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    state = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_direct_calls", x => x.id);
                    table.CheckConstraint("ck_direct_calls_distinct_participants", "caller_id <> callee_id");
                    table.CheckConstraint("ck_direct_calls_kind", "kind IN ('Voice', 'Video')");
                    table.CheckConstraint("ck_direct_calls_state", "state IN ('Ringing', 'Accepted', 'Declined', 'Canceled', 'Missed', 'Ended')");
                    table.CheckConstraint("ck_direct_calls_times", "created_at < expires_at AND (state IN ('Accepted', 'Ended')) = (accepted_at IS NOT NULL) AND (state IN ('Declined', 'Canceled', 'Missed', 'Ended')) = (ended_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_direct_calls_app_sessions_callee_session_id",
                        column: x => x.callee_session_id,
                        principalTable: "app_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_direct_calls_app_sessions_caller_session_id",
                        column: x => x.caller_session_id,
                        principalTable: "app_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_direct_calls_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_direct_calls_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_direct_calls_users_callee_id",
                        column: x => x.callee_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_direct_calls_users_caller_id",
                        column: x => x.caller_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_meetings_direct_call",
                table: "meetings",
                column: "direct_call_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_meetings_single_source",
                table: "meetings",
                sql: "num_nonnulls(calendar_event_id, conversation_id, direct_call_id) = 1");

            migrationBuilder.CreateIndex(
                name: "ix_direct_calls_access_check",
                table: "direct_calls",
                columns: new[] { "checked_at", "id" },
                filter: "state IN ('Ringing', 'Accepted')");

            migrationBuilder.CreateIndex(
                name: "ix_direct_calls_callee_created",
                table: "direct_calls",
                columns: new[] { "callee_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_direct_calls_callee_session_id",
                table: "direct_calls",
                column: "callee_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_direct_calls_caller_created",
                table: "direct_calls",
                columns: new[] { "caller_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_direct_calls_caller_session_id",
                table: "direct_calls",
                column: "caller_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_direct_calls_conversation_id_created_at",
                table: "direct_calls",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_direct_calls_organization_id",
                table: "direct_calls",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_direct_calls_pending_ring",
                table: "direct_calls",
                columns: new[] { "expires_at", "id" },
                filter: "state = 'Ringing'");

            migrationBuilder.CreateIndex(
                name: "ux_direct_calls_active_conversation",
                table: "direct_calls",
                column: "conversation_id",
                unique: true,
                filter: "state IN ('Ringing', 'Accepted')");

            migrationBuilder.CreateIndex(
                name: "ux_direct_calls_caller_request",
                table: "direct_calls",
                columns: new[] { "caller_id", "client_call_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_meetings_direct_calls_direct_call_id",
                table: "meetings",
                column: "direct_call_id",
                principalTable: "direct_calls",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_meetings_direct_calls_direct_call_id",
                table: "meetings");

            migrationBuilder.DropTable(
                name: "direct_calls");

            migrationBuilder.DropIndex(
                name: "ux_meetings_direct_call",
                table: "meetings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_meetings_single_source",
                table: "meetings");

            migrationBuilder.DropColumn(
                name: "direct_call_id",
                table: "meetings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_meetings_single_source",
                table: "meetings",
                sql: "(calendar_event_id IS NULL) <> (conversation_id IS NULL)");
        }
    }
}
