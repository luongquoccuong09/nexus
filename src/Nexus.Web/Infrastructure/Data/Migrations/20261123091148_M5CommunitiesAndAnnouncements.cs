using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M5CommunitiesAndAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "community_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_community_main_feed",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "posting_policy",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "channel_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    is_announcement = table.Column<bool>(type: "boolean", nullable: false),
                    requires_acknowledgment = table.Column<bool>(type: "boolean", nullable: false),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    pinned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pinned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_posts", x => x.id);
                    table.ForeignKey(
                        name: "FK_channel_posts_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_channel_posts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_posts_users_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "communities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_official = table.Column<bool>(type: "boolean", nullable: false),
                    mapped_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_communities", x => x.id);
                    table.ForeignKey(
                        name: "FK_communities_departments_mapped_department_id",
                        column: x => x.mapped_department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_communities_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_communities_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "announcement_acknowledgments",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_acknowledgments", x => new { x.post_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_announcement_acknowledgments_channel_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "channel_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_announcement_acknowledgments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "polls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    options_json = table.Column<string>(type: "text", nullable: false),
                    allow_multiple = table.Column<bool>(type: "boolean", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_polls", x => x.id);
                    table.ForeignKey(
                        name: "FK_polls_channel_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "channel_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_comments", x => x.id);
                    table.ForeignKey(
                        name: "FK_post_comments_channel_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "channel_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_post_comments_users_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "post_reactions",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    emoji = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_reactions", x => new { x.post_id, x.user_id, x.emoji });
                    table.ForeignKey(
                        name: "FK_post_reactions_channel_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "channel_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_post_reactions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_members",
                columns: table => new
                {
                    community_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_members", x => new { x.community_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_community_members_communities_community_id",
                        column: x => x.community_id,
                        principalTable: "communities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_community_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "poll_votes",
                columns: table => new
                {
                    poll_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    voted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_votes", x => new { x.poll_id, x.user_id, x.option_id });
                    table.ForeignKey(
                        name: "FK_poll_votes_polls_poll_id",
                        column: x => x.poll_id,
                        principalTable: "polls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_poll_votes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_community_feed",
                table: "conversations",
                columns: new[] { "community_id", "is_community_main_feed" });

            migrationBuilder.CreateIndex(
                name: "IX_announcement_acknowledgments_user_id",
                table: "announcement_acknowledgments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_channel_posts_author_user_id",
                table: "channel_posts",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_posts_conv_pinned_created",
                table: "channel_posts",
                columns: new[] { "conversation_id", "is_pinned", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_posts_organization_id",
                table: "channel_posts",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_communities_created_by_user_id",
                table: "communities",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_communities_mapped_department_id",
                table: "communities",
                column: "mapped_department_id");

            migrationBuilder.CreateIndex(
                name: "ix_communities_org_visibility_name",
                table: "communities",
                columns: new[] { "organization_id", "visibility", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_community_members_user_active",
                table: "community_members",
                columns: new[] { "user_id", "left_at" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_votes_user_id",
                table: "poll_votes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_polls_post_id",
                table: "polls",
                column: "post_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_post_comments_author_user_id",
                table: "post_comments",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_comments_post_created",
                table: "post_comments",
                columns: new[] { "post_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_post_reactions_user_id",
                table: "post_reactions",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_communities_community_id",
                table: "conversations",
                column: "community_id",
                principalTable: "communities",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversations_communities_community_id",
                table: "conversations");

            migrationBuilder.DropTable(
                name: "announcement_acknowledgments");

            migrationBuilder.DropTable(
                name: "community_members");

            migrationBuilder.DropTable(
                name: "poll_votes");

            migrationBuilder.DropTable(
                name: "post_comments");

            migrationBuilder.DropTable(
                name: "post_reactions");

            migrationBuilder.DropTable(
                name: "communities");

            migrationBuilder.DropTable(
                name: "polls");

            migrationBuilder.DropTable(
                name: "channel_posts");

            migrationBuilder.DropIndex(
                name: "ix_conversations_community_feed",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "community_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "is_community_main_feed",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "posting_policy",
                table: "conversations");
        }
    }
}
