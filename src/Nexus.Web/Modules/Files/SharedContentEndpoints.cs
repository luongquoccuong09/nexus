using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Files;

/// <summary>
/// Unified Shared View discovery endpoints (Files, Links, Media) for both Chat and Channels (M6.32).
/// Strictly enforces live membership, VisibleFromSeq (SEC-18), SEC-20b, soft-deletion, and <= 50 pagination.
/// </summary>
public static class SharedContentEndpoints
{
    public const int MaxQueryLength = 100;

    public static IEndpointRouteBuilder MapSharedContentEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        app.MapGet(prefix + "/conversations/{conversationId:guid}/shared", HandleListSharedAsync);
        if (string.Equals(prefix, OrganizationScope.Api, StringComparison.Ordinal))
        {
            app.MapGet(prefix + "/channels/{conversationId:guid}/shared", HandleListSharedAsync);
        }

        return app;
    }

    private static async Task<IResult> HandleListSharedAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<FilePolicyOptions> policyOptions,
        string? tab = "files",
        string? q = null,
        DateTimeOffset? before = null,
        Guid? beforeId = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var normalizedTab = string.IsNullOrWhiteSpace(tab) ? "files" : tab.Trim().ToLowerInvariant();
        if (normalizedTab is not ("files" or "links" or "media"))
        {
            return Results.BadRequest(new { code = "invalid_shared_tab", message = "Tab must be files, links, or media." });
        }

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        if (beforeId.HasValue && !before.HasValue)
        {
            return Results.BadRequest(new { code = "invalid_cursor", message = "before is required when beforeId is supplied." });
        }

        var search = q?.Trim();
        if (search is not null && search.Length > MaxQueryLength)
        {
            return Results.BadRequest(new { code = "invalid_query", message = $"Search query must be at most {MaxQueryLength} characters." });
        }

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var isChannel = access.Conversation.Kind == ConversationKind.Channel;
        var isArchived = access.Conversation.ArchivedAt is not null;
        var likePattern = string.IsNullOrEmpty(search)
            ? null
            : $"%{EscapeLike(search)}%";

        if (normalizedTab == "links")
        {
            return await HandleLinksTabAsync(db, access, conversationId, isChannel, isArchived, likePattern, before, beforeId, pageSize, ct);
        }

        var mediaOnly = normalizedTab == "media";
        return await HandleAttachmentsTabAsync(db, access, actorId, conversationId, normalizedTab, mediaOnly, isChannel, isArchived, likePattern, before, beforeId, pageSize, policyOptions.Value, ct);
    }

    private static async Task<IResult> HandleAttachmentsTabAsync(
        NexusDbContext db,
        ResolvedConversationAccess access,
        Guid actorId,
        Guid conversationId,
        string tab,
        bool mediaOnly,
        bool isChannel,
        bool isArchived,
        string? likePattern,
        DateTimeOffset? before,
        Guid? beforeId,
        int pageSize,
        FilePolicyOptions policy,
        CancellationToken ct)
    {
        var orgId = access.Conversation.OrganizationId;
        var baseAttachments = db.Attachments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.State == AttachmentState.Attached);

        if (mediaOnly)
        {
            baseAttachments = baseAttachments.Where(a =>
                EF.Functions.ILike(a.ContentType, "image/%") ||
                EF.Functions.ILike(a.ContentType, "video/%") ||
                EF.Functions.ILike(a.ContentType, "audio/%"));
        }

        if (likePattern is not null)
        {
            baseAttachments = baseAttachments.Where(a => EF.Functions.ILike(a.FileName, likePattern));
        }

        if (before is { } timestamp)
        {
            var utc = timestamp.ToUniversalTime();
            baseAttachments = beforeId is { } id
                ? baseAttachments.Where(a => a.CreatedAt < utc || (a.CreatedAt == utc && a.Id.CompareTo(id) < 0))
                : baseAttachments.Where(a => a.CreatedAt < utc);
        }

        List<object> items;
        bool hasMore;
        DateTimeOffset? nextBefore = null;
        Guid? nextBeforeId = null;

        if (isChannel)
        {
            var rows = await (
                from a in baseAttachments
                join p in db.ChannelPosts.AsNoTracking() on a.PostId equals p.Id
                join u in db.OrganizationPeople.AsNoTracking() on a.UploaderUserId equals u.Id into uploaderJoin
                from uploader in uploaderJoin.DefaultIfEmpty()
                where a.PostId != null && p.ConversationId == conversationId && p.OrganizationId == orgId && p.DeletedAt == null
                orderby a.CreatedAt descending, a.Id descending
                select new
                {
                    Attachment = a,
                    PostId = p.Id,
                    PostTitle = p.Title,
                    SenderDisplayName = uploader != null ? uploader.DisplayName : "Member"
                })
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasMore = rows.Count > pageSize;
            if (hasMore) rows.RemoveAt(pageSize);

            if (rows.Count > 0)
            {
                nextBefore = rows[^1].Attachment.CreatedAt;
                nextBeforeId = rows[^1].Attachment.Id;
            }

            items = rows.Select(x => ProjectAttachmentItem(
                x.Attachment,
                x.SenderDisplayName,
                messageId: null,
                postId: x.PostId,
                postTitle: x.PostTitle,
                sourceLink: ContentLinkCodec.Encode("p", orgId, conversationId, x.PostId))).ToList();
        }
        else
        {
            var rows = await (
                from a in baseAttachments
                join m in MessageHistory.VisibleAfter(db, access.VisibleFromSeq) on a.MessageId equals m.Id
                join u in db.OrganizationPeople.AsNoTracking() on a.UploaderUserId equals u.Id into uploaderJoin
                from uploader in uploaderJoin.DefaultIfEmpty()
                where a.MessageId != null && m.ConversationId == conversationId && m.DeletedAt == null
                orderby a.CreatedAt descending, a.Id descending
                select new
                {
                    Attachment = a,
                    MessageId = m.Id,
                    SenderDisplayName = uploader != null ? uploader.DisplayName : "Member"
                })
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasMore = rows.Count > pageSize;
            if (hasMore) rows.RemoveAt(pageSize);

            if (rows.Count > 0)
            {
                nextBefore = rows[^1].Attachment.CreatedAt;
                nextBeforeId = rows[^1].Attachment.Id;
            }

            items = rows.Select(x => ProjectAttachmentItem(
                x.Attachment,
                x.SenderDisplayName,
                messageId: x.MessageId,
                postId: null,
                postTitle: null,
                sourceLink: ContentLinkCodec.Encode("m", orgId, conversationId, x.MessageId))).ToList();
        }

        object? quota = tab == "files"
            ? await FilesEndpoints.BuildQuotaSummaryAsync(db, actorId, conversationId, policy, ct)
            : null;

        return Results.Ok(new
        {
            tab,
            items,
            hasMore,
            nextBefore,
            nextBeforeId,
            isArchived,
            quota
        });
    }

    private static async Task<IResult> HandleLinksTabAsync(
        NexusDbContext db,
        ResolvedConversationAccess access,
        Guid conversationId,
        bool isChannel,
        bool isArchived,
        string? likePattern,
        DateTimeOffset? before,
        Guid? beforeId,
        int pageSize,
        CancellationToken ct)
    {
        var orgId = access.Conversation.OrganizationId;
        var baseLinks = db.SharedLinks.AsNoTracking()
            .Where(l => l.ConversationId == conversationId);

        if (likePattern is not null)
        {
            baseLinks = baseLinks.Where(l =>
                EF.Functions.ILike(l.Url, likePattern) ||
                EF.Functions.ILike(l.Host, likePattern) ||
                (l.Label != null && EF.Functions.ILike(l.Label, likePattern)));
        }

        if (before is { } timestamp)
        {
            var utc = timestamp.ToUniversalTime();
            baseLinks = beforeId is { } id
                ? baseLinks.Where(l => l.CreatedAt < utc || (l.CreatedAt == utc && l.Id.CompareTo(id) < 0))
                : baseLinks.Where(l => l.CreatedAt < utc);
        }

        List<object> items;
        bool hasMore;
        DateTimeOffset? nextBefore = null;
        Guid? nextBeforeId = null;

        if (isChannel)
        {
            var rows = await (
                from l in baseLinks
                join p in db.ChannelPosts.AsNoTracking() on l.PostId equals p.Id
                join u in db.OrganizationPeople.AsNoTracking() on l.SenderUserId equals u.Id into senderJoin
                from sender in senderJoin.DefaultIfEmpty()
                where l.PostId != null && p.ConversationId == conversationId && p.OrganizationId == orgId && p.DeletedAt == null
                orderby l.CreatedAt descending, l.Id descending
                select new
                {
                    Link = l,
                    PostId = p.Id,
                    PostTitle = p.Title,
                    SenderDisplayName = sender != null ? sender.DisplayName : "Member"
                })
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasMore = rows.Count > pageSize;
            if (hasMore) rows.RemoveAt(pageSize);

            if (rows.Count > 0)
            {
                nextBefore = rows[^1].Link.CreatedAt;
                nextBeforeId = rows[^1].Link.Id;
            }

            items = rows.Select(x => (object)new
            {
                id = x.Link.Id,
                conversationId = x.Link.ConversationId,
                senderUserId = x.Link.SenderUserId,
                senderDisplayName = x.SenderDisplayName,
                createdAt = x.Link.CreatedAt,
                messageId = (Guid?)null,
                postId = (Guid?)x.PostId,
                postTitle = x.PostTitle,
                sourceLink = ContentLinkCodec.Encode("p", orgId, conversationId, x.PostId),
                url = x.Link.Url,
                host = x.Link.Host,
                label = x.Link.Label
            }).ToList();
        }
        else
        {
            var rows = await (
                from l in baseLinks
                join m in MessageHistory.VisibleAfter(db, access.VisibleFromSeq) on l.MessageId equals m.Id
                join u in db.OrganizationPeople.AsNoTracking() on l.SenderUserId equals u.Id into senderJoin
                from sender in senderJoin.DefaultIfEmpty()
                where l.MessageId != null && m.ConversationId == conversationId && m.DeletedAt == null
                orderby l.CreatedAt descending, l.Id descending
                select new
                {
                    Link = l,
                    MessageId = m.Id,
                    SenderDisplayName = sender != null ? sender.DisplayName : "Member"
                })
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasMore = rows.Count > pageSize;
            if (hasMore) rows.RemoveAt(pageSize);

            if (rows.Count > 0)
            {
                nextBefore = rows[^1].Link.CreatedAt;
                nextBeforeId = rows[^1].Link.Id;
            }

            items = rows.Select(x => (object)new
            {
                id = x.Link.Id,
                conversationId = x.Link.ConversationId,
                senderUserId = x.Link.SenderUserId,
                senderDisplayName = x.SenderDisplayName,
                createdAt = x.Link.CreatedAt,
                messageId = (Guid?)x.MessageId,
                postId = (Guid?)null,
                postTitle = (string?)null,
                sourceLink = ContentLinkCodec.Encode("m", orgId, conversationId, x.MessageId),
                url = x.Link.Url,
                host = x.Link.Host,
                label = x.Link.Label
            }).ToList();
        }

        return Results.Ok(new
        {
            tab = "links",
            items,
            hasMore,
            nextBefore,
            nextBeforeId,
            isArchived,
            quota = (object?)null
        });
    }

    private static object ProjectAttachmentItem(
        AttachmentEntity attachment,
        string senderDisplayName,
        Guid? messageId,
        Guid? postId,
        string? postTitle,
        string sourceLink)
    {
        var dto = AttachmentProjection.ToAttachmentDto(attachment);
        return new
        {
            id = dto.Id,
            conversationId = dto.ConversationId,
            senderUserId = dto.UploaderUserId,
            senderDisplayName,
            createdAt = dto.CreatedAt,
            messageId,
            postId,
            postTitle,
            sourceLink,
            fileName = dto.FileName,
            contentType = dto.ContentType,
            sizeBytes = dto.SizeBytes,
            width = dto.ImageWidth,
            height = dto.ImageHeight,
            durationMs = dto.DurationMs,
            hasThumbnail = dto.ThumbnailUrl is not null,
            downloadUrl = dto.DownloadUrl,
            thumbnailUrl = dto.ThumbnailUrl,
            previewUrl = dto.PreviewUrl,
            audioUrl = dto.AudioUrl
        };
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
