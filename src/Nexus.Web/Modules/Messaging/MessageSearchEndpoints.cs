using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public static class MessageSearchEndpoints
{
    private sealed record SearchCursor(DateTimeOffset CreatedAt, Guid Id, string Kind);

    private sealed class SearchRow
    {
        public Guid Id { get; set; }
        public required string Kind { get; set; }
        public Guid ConversationId { get; set; }
        public Guid AuthorId { get; set; }
        public required string AuthorName { get; set; }
        public required string ConversationKind { get; set; }
        public required string ConversationTitle { get; set; }
        public required string MembershipRole { get; set; }
        public bool Official { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? Title { get; set; }
        public required string Body { get; set; }
        public bool HasAttachment { get; set; }
    }

    public static IEndpointRouteBuilder MapMessageSearchEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        app.MapGet(prefix + "/messages/search", SearchAsync);
        return app;
    }

    private static async Task<IResult> SearchAsync(HttpContext ctx, [FromServices] NexusDbContext db, IAuthorizationService authz,
        string? q, Guid? senderId, string? sender, Guid? conversationId, DateTimeOffset? from, DateTimeOffset? to, bool? hasAttachment,
        string? cursor, int? limit, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(userId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        if (!PaginationGuard.TryDecodeCursor<SearchCursor>(cursor, out var before) ||
            before is not null && (before.Id == Guid.Empty || before.Kind is not ("m" or "p")))
            return PaginationGuard.InvalidCursor();
        if (q?.Length > 120 || sender?.Length > 120 || from.HasValue && to.HasValue && from >= to)
            return Results.BadRequest(new { code = "invalid_search_filters", message = "Use a query of up to 120 characters and a valid date range." });

        var term = HrCsvImportService.NormalizeVietnamese(q ?? string.Empty);
        var literal = LikePattern(term);
        var senderName = HrCsvImportService.NormalizeVietnamese(sender?.Trim().TrimStart('@') ?? string.Empty);
        var senderLiteral = LikePattern(senderName);
        var scopeId = db.ConversationOrganizationId;
        var actorContext = actor.ToActorContext();
        var rows = await db.Database.SqlQuery<SearchRow>($"""
            WITH filters AS NOT MATERIALIZED (
                SELECT CAST({scopeId} AS uuid) AS scope_id, CAST({term} AS text) AS term,
                       CAST({literal} AS text) AS literal, CAST({senderId} AS uuid) AS sender_id,
                       CAST({conversationId} AS uuid) AS conversation_id,
                       CAST({from?.ToUniversalTime()} AS timestamptz) AS from_at,
                       CAST({to?.ToUniversalTime()} AS timestamptz) AS to_at
            ), viewer AS NOT MATERIALIZED (
                SELECT account."Id" AS id, membership.trust_level
                FROM users AS account CROSS JOIN filters AS f
                LEFT JOIN organization_memberships AS membership
                    ON membership.user_id = account."Id" AND membership.organization_id = f.scope_id
                WHERE account."Id" = {userId} AND account.status = 'Active'
                  AND (f.scope_id IS NULL OR membership.status = 'Active')
            ), hits AS (
                SELECT m.id, 'm'::text AS kind, c.id AS conversation_id, m.sender_id AS author_id,
                       c.kind AS conversation_kind, COALESCE(c.title, 'Direct message') AS conversation_title,
                       cm.role AS membership_role, FALSE AS official, m.created_at, NULL::text AS title, m.body,
                       EXISTS (SELECT 1 FROM attachments a WHERE a.message_id = m.id
                           AND a.conversation_id = c.id AND a.state = 'Attached'
                           AND a.scan_status IN ('clean', 'skipped_prototype')) AS has_attachment
                FROM messages m
                JOIN conversations c ON c.id = m.conversation_id
                JOIN conversation_members cm ON cm.conversation_id = c.id AND cm.left_at IS NULL
                JOIN viewer v ON v.id = cm.user_id
                CROSS JOIN filters f
                WHERE c.organization_id IS NOT DISTINCT FROM f.scope_id AND c.kind IN ('Dm', 'Group', 'Self')
                  AND (c.kind <> 'Self' OR (c.created_by_user_id = v.id AND cm.role = 'Owner'))
                  AND m.deleted_at IS NULL AND m.seq > cm.visible_from_seq
                  AND (m.parent_message_id IS NULL OR EXISTS (SELECT 1 FROM messages AS parent
                      WHERE parent.id = m.parent_message_id AND parent.conversation_id = c.id AND parent.seq > cm.visible_from_seq))
                  AND (f.conversation_id IS NULL OR c.id = f.conversation_id)
                  AND (f.sender_id IS NULL OR m.sender_id = f.sender_id)
                  AND (f.from_at IS NULL OR m.created_at >= f.from_at)
                  AND (f.to_at IS NULL OR m.created_at < f.to_at)
                  AND (f.term = '' OR to_tsvector('simple'::regconfig, public.immutable_unaccent(m.body)) @@ plainto_tsquery('simple'::regconfig, f.term)
                       OR public.immutable_unaccent(lower(m.body)) LIKE f.literal)
                UNION ALL
                SELECT p.id, 'p'::text AS kind, c.id AS conversation_id, p.author_user_id AS author_id,
                       c.kind AS conversation_kind, COALESCE(c.title, 'Channel') AS conversation_title,
                       cm.role AS membership_role, community.is_official AS official, p.created_at, p.title, p.body,
                       EXISTS (SELECT 1 FROM attachments a WHERE a.post_id = p.id
                           AND a.conversation_id = c.id AND a.state = 'Attached'
                           AND a.scan_status IN ('clean', 'skipped_prototype')) AS has_attachment
                FROM channel_posts p
                JOIN conversations c ON c.id = p.conversation_id AND c.kind = 'Channel'
                JOIN communities community ON community.id = c.community_id
                JOIN community_members cm ON cm.community_id = community.id AND cm.left_at IS NULL
                JOIN viewer v ON v.id = cm.user_id
                CROSS JOIN filters f
                WHERE p.organization_id = f.scope_id AND c.organization_id = f.scope_id AND community.organization_id = f.scope_id
                  AND (NOT community.is_official OR v.trust_level = 'Verified') AND p.deleted_at IS NULL
                  AND (f.conversation_id IS NULL OR c.id = f.conversation_id)
                  AND (f.sender_id IS NULL OR p.author_user_id = f.sender_id)
                  AND (f.from_at IS NULL OR p.created_at >= f.from_at)
                  AND (f.to_at IS NULL OR p.created_at < f.to_at)
                  AND (f.term = '' OR to_tsvector('simple'::regconfig,
                         public.immutable_unaccent(COALESCE(p.title, '') || ' ' || p.body)) @@ plainto_tsquery('simple'::regconfig, f.term)
                       OR public.immutable_unaccent(lower(COALESCE(p.title, '') || ' ' || p.body)) LIKE f.literal)
            )
            SELECT h.id AS "Id", h.kind AS "Kind", h.conversation_id AS "ConversationId", h.author_id AS "AuthorId",
                   COALESCE(author_membership.display_name, author.display_name, 'Member') AS "AuthorName",
                   h.conversation_kind AS "ConversationKind", h.conversation_title AS "ConversationTitle",
                   h.membership_role AS "MembershipRole", h.official AS "Official", h.created_at AS "CreatedAt",
                   h.title AS "Title", h.body AS "Body", h.has_attachment AS "HasAttachment"
            FROM hits h CROSS JOIN filters f
            LEFT JOIN users author ON author."Id" = h.author_id
            LEFT JOIN organization_memberships author_membership ON author_membership.user_id = h.author_id AND author_membership.organization_id = f.scope_id
            WHERE (CAST({hasAttachment} AS boolean) IS NULL OR h.has_attachment = CAST({hasAttachment} AS boolean))
              AND ({senderName} = '' OR public.immutable_unaccent(lower(COALESCE(author_membership.display_name, author.display_name, ''))) LIKE {senderLiteral}
                    OR public.immutable_unaccent(lower(COALESCE(author_membership.username, author."UserName", ''))) LIKE {senderLiteral})
              AND (CAST({before?.CreatedAt.ToUniversalTime()} AS timestamptz) IS NULL OR
                    (h.created_at, h.id, h.kind) < (CAST({before?.CreatedAt.ToUniversalTime()} AS timestamptz),
                        CAST({before?.Id} AS uuid), CAST({before?.Kind} AS text)))
            ORDER BY h.created_at DESC, h.id DESC, h.kind DESC
            LIMIT {pageSize + 1}
            """).ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            items = rows.Where(row => Enum.TryParse<ConversationKind>(row.ConversationKind, out var kind) &&
                Enum.TryParse<ResourceRole>(row.MembershipRole, out var role) &&
                authz.Can(actorContext, ResourceAction.MessageSearch, new ResourceContext(kind.ToResourceKind(), actor.OrganizationId,
                    MembershipRole: role, IsOfficial: row.Official, TargetIsSelf: kind == ConversationKind.Self)))
                .Select(row => new
                {
                    row.Id, kind = row.Kind == "m" ? "message" : "post", row.ConversationId, row.ConversationTitle,
                    row.AuthorId, row.AuthorName, row.CreatedAt, row.Title, row.HasAttachment,
                    preview = ConversationEndpoints.FormatPreview(row.Body, "[Attachment]"),
                    sourceLink = ContentLinkCodec.Encode(row.Kind, scopeId, row.ConversationId, row.Id)
                }),
            nextCursor = hasMore ? PaginationGuard.EncodeCursor(new SearchCursor(rows[^1].CreatedAt, rows[^1].Id, rows[^1].Kind)) : null
        });
    }

    private static string LikePattern(string value) => "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
