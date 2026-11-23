using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1bDirectoryAndProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_object_key",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "custom_status",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.immutable_unaccent(input text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE
                PARALLEL SAFE
                STRICT
                AS $$
                    SELECT replace(replace(public.unaccent('public.unaccent', input), 'đ', 'd'), 'Đ', 'D');
                $$;

                CREATE INDEX ix_users_display_name_trgm
                    ON users USING gin (public.immutable_unaccent(lower(display_name)) gin_trgm_ops);

                CREATE INDEX ix_users_username_trgm
                    ON users USING gin (public.immutable_unaccent(lower("UserName")) gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_users_username_trgm;
                DROP INDEX IF EXISTS ix_users_display_name_trgm;
                DROP FUNCTION IF EXISTS public.immutable_unaccent(text);
                """);

            migrationBuilder.DropColumn(
                name: "avatar_object_key",
                table: "users");

            migrationBuilder.DropColumn(
                name: "custom_status",
                table: "users");
        }
    }
}
