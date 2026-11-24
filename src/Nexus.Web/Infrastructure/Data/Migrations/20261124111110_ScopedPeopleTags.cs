using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopedPeopleTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "people_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    community_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people_tags", x => x.id);
                    table.CheckConstraint("ck_people_tags_lengths", "char_length(name) BETWEEN 2 AND 48 AND (description IS NULL OR char_length(description) <= 200)");
                    table.CheckConstraint("ck_people_tags_slug_format", "slug ~ '^[a-z0-9][a-z0-9_-]{1,31}$'");
                    table.ForeignKey(
                        name: "FK_people_tags_communities_community_id",
                        column: x => x.community_id,
                        principalTable: "communities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_people_tags_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_people_tags_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "people_tag_members",
                columns: table => new
                {
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_people_tag_members", x => new { x.tag_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_people_tag_members_people_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "people_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_people_tag_members_users_added_by_user_id",
                        column: x => x.added_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_people_tag_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_people_tag_members_added_by_user_id",
                table: "people_tag_members",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_people_tag_members_user_id",
                table: "people_tag_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_people_tags_community_id",
                table: "people_tags",
                column: "community_id");

            migrationBuilder.CreateIndex(
                name: "IX_people_tags_created_by_user_id",
                table: "people_tags",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_people_tags_scope_slug",
                table: "people_tags",
                columns: new[] { "organization_id", "community_id", "slug" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "people_tag_members");

            migrationBuilder.DropTable(
                name: "people_tags");
        }
    }
}
