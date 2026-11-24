using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX ix_messages_search_fts ON messages
                    USING gin (to_tsvector('simple'::regconfig, public.immutable_unaccent(body)))
                    WHERE deleted_at IS NULL;
                CREATE INDEX ix_messages_search_trgm ON messages
                    USING gin (public.immutable_unaccent(lower(body)) gin_trgm_ops)
                    WHERE deleted_at IS NULL;
                CREATE INDEX ix_channel_posts_search_fts ON channel_posts
                    USING gin (to_tsvector('simple'::regconfig, public.immutable_unaccent(COALESCE(title, '') || ' ' || body)))
                    WHERE deleted_at IS NULL;
                CREATE INDEX ix_channel_posts_search_trgm ON channel_posts
                    USING gin (public.immutable_unaccent(lower(COALESCE(title, '') || ' ' || body)) gin_trgm_ops)
                    WHERE deleted_at IS NULL;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX ix_channel_posts_search_trgm;
                DROP INDEX ix_channel_posts_search_fts;
                DROP INDEX ix_messages_search_trgm;
                DROP INDEX ix_messages_search_fts;
                """);

        }
    }
}
