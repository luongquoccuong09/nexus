using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CalendarRecurrence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_meetings_calendar_event",
                table: "meetings");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "calendar_occurrence_utc",
                table: "meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recurrence_frequency",
                table: "calendar_events",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "recurrence_interval",
                table: "calendar_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "recurrence_until_local_date",
                table: "calendar_events",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reminder_generation_id",
                table: "calendar_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_occurrence_overrides",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_occurrence_overrides", x => new { x.event_id, x.original_start_utc });
                    table.CheckConstraint("ck_calendar_occurrence_overrides_shape", "(start_utc IS NULL AND end_utc IS NULL AND title IS NULL AND canceled_at IS NOT NULL) OR (start_utc IS NOT NULL AND end_utc IS NOT NULL AND title IS NOT NULL AND start_utc < end_utc)");
                    table.ForeignKey(
                        name: "FK_calendar_occurrence_overrides_calendar_events_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_occurrence_reminders",
                columns: table => new
                {
                    outbox_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reminder_minutes = table.Column<int>(type: "integer", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_occurrence_reminders", x => x.outbox_message_id);
                    table.ForeignKey(
                        name: "FK_calendar_occurrence_reminders_calendar_events_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calendar_occurrence_reminders_outbox_messages_outbox_messag~",
                        column: x => x.outbox_message_id,
                        principalTable: "outbox_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_meetings_calendar_occurrence",
                table: "meetings",
                columns: new[] { "calendar_event_id", "calendar_occurrence_utc" },
                unique: true,
                filter: "calendar_event_id IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_meetings_occurrence_source",
                table: "meetings",
                sql: "calendar_occurrence_utc IS NULL OR calendar_event_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_calendar_events_recurrence",
                table: "calendar_events",
                sql: "(recurrence_frequency IS NULL AND recurrence_interval IS NULL AND recurrence_until_local_date IS NULL) OR (recurrence_frequency IN ('Daily', 'Weekly', 'Monthly') AND recurrence_frequency IS NOT NULL AND recurrence_interval IS NOT NULL AND recurrence_interval BETWEEN 1 AND 365)");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_occurrence_overrides_moved",
                table: "calendar_occurrence_overrides",
                columns: new[] { "event_id", "start_utc", "end_utc" },
                filter: "start_utc IS NOT NULL AND canceled_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_occurrence_reminders_time",
                table: "calendar_occurrence_reminders",
                columns: new[] { "event_id", "original_start_utc", "effective_start_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_occurrence_overrides");

            migrationBuilder.DropTable(
                name: "calendar_occurrence_reminders");

            migrationBuilder.DropIndex(
                name: "ux_meetings_calendar_occurrence",
                table: "meetings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_meetings_occurrence_source",
                table: "meetings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_calendar_events_recurrence",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "calendar_occurrence_utc",
                table: "meetings");

            migrationBuilder.DropColumn(
                name: "recurrence_frequency",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "recurrence_interval",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "recurrence_until_local_date",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "reminder_generation_id",
                table: "calendar_events");

            migrationBuilder.CreateIndex(
                name: "ux_meetings_calendar_event",
                table: "meetings",
                column: "calendar_event_id",
                unique: true);
        }
    }
}
