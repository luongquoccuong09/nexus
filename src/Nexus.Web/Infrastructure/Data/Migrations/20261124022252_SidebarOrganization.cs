using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SidebarOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_sidebar_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    surface = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sidebar_sections", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_sidebar_sections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sidebar_items",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    surface = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_favorite = table.Column<bool>(type: "boolean", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_hidden = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sidebar_items", x => new { x.user_id, x.surface, x.target_id });
                    table.ForeignKey(
                        name: "FK_user_sidebar_items_user_sidebar_sections_section_id",
                        column: x => x.section_id,
                        principalTable: "user_sidebar_sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_user_sidebar_items_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_sidebar_items_section_id",
                table: "user_sidebar_items",
                column: "section_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_sidebar_items_user_surface_fav_order",
                table: "user_sidebar_items",
                columns: new[] { "user_id", "surface", "is_favorite", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_user_sidebar_sections_user_surface_order",
                table: "user_sidebar_sections",
                columns: new[] { "user_id", "surface", "sort_order", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_sidebar_items");

            migrationBuilder.DropTable(
                name: "user_sidebar_sections");
        }
    }
}
