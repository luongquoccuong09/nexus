using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CalendarEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_communities_organization_id_id",
                table: "communities",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "calendar_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    community_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reminder_minutes = table.Column<int>(type: "integer", nullable: true),
                    reminder_outbox_id = table.Column<Guid>(type: "uuid", nullable: true),
                    canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    creation_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    creation_request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_events", x => x.id);
                    table.CheckConstraint("ck_calendar_events_community_scope", "community_id IS NULL OR organization_id IS NOT NULL");
                    table.CheckConstraint("ck_calendar_events_reminder", "reminder_minutes BETWEEN 0 AND 10080");
                    table.CheckConstraint("ck_calendar_events_time_range", "start_utc < end_utc");
                    table.ForeignKey(
                        name: "FK_calendar_events_communities_organization_id_community_id",
                        columns: x => new { x.organization_id, x.community_id },
                        principalTable: "communities",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_calendar_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_calendar_events_users_organizer_id",
                        column: x => x.organizer_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "calendar_attendees",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_attendees", x => new { x.event_id, x.user_id });
                    table.CheckConstraint("ck_calendar_attendees_status", "status IN ('Going', 'Tentative', 'Declined')");
                    table.ForeignKey(
                        name: "FK_calendar_attendees_calendar_events_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calendar_attendees_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_attendees_user_event",
                table: "calendar_attendees",
                columns: new[] { "user_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_events_organization_id_community_id",
                table: "calendar_events",
                columns: new[] { "organization_id", "community_id" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_scope_range",
                table: "calendar_events",
                columns: new[] { "organization_id", "start_utc", "end_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_calendar_events_scope_creation",
                table: "calendar_events",
                columns: new[] { "organizer_id", "organization_id", "creation_key_hash" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_attendees");

            migrationBuilder.DropTable(
                name: "calendar_events");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_communities_organization_id_id",
                table: "communities");
        }
    }
}
