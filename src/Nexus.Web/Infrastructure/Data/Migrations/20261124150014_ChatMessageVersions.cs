using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatMessageVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "messages",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                CREATE INDEX ix_conversation_events_conv_message_subject
                ON conversation_events (conversation_id, (CASE event_type
                    WHEN 'message.created' THEN payload_json::jsonb ->> 'id'
                    WHEN 'message.thread_reply.created' THEN payload_json::jsonb -> 'message' ->> 'id'
                    ELSE payload_json::jsonb ->> 'messageId'
                END))
                WHERE event_type IN ('message.created', 'message.thread_reply.created', 'message.edited',
                                     'message.reaction.added', 'message.reaction.removed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_conversation_events_conv_message_subject");

            migrationBuilder.DropColumn(
                name: "version",
                table: "messages");
        }
    }
}
