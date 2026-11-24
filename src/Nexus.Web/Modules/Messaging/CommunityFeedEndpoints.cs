using Nexus.Web.Modules.Org;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Notifications;

namespace Nexus.Web.Modules.Messaging;

public static partial class CommunityFeedEndpoints
{
    private sealed record PostCursor(bool IsPinned, DateTimeOffset CreatedAt, Guid Id);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public sealed record CreatePollInput(string Question, string[] Options, bool AllowMultiple = false, DateTimeOffset? ClosesAt = null);
    public sealed record CreatePostRequest(string? Title, string? Body, bool IsAnnouncement = false, bool RequiresAcknowledgment = false, CreatePollInput? Poll = null, Guid[]? AttachmentIds = null);
    public sealed record CreateCommentRequest(string Body);
    public sealed record PinPostRequest(bool IsPinned);
    public sealed record VotePollRequest(string[] OptionIds);
    public sealed record PollOptionSpec(string Id, string Label);

    public static IEndpointRouteBuilder MapCommunityFeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(OrganizationScope.Api + "/channels/{conversationId:guid}/posts", HandleListPostsAsync);
        app.MapPost(OrganizationScope.Api + "/channels/{conversationId:guid}/posts", HandleCreatePostAsync);

        var posts = app.MapGroup(OrganizationScope.Api + "/posts/{postId:guid}");
        posts.MapPut("/pin", HandlePinPostAsync);
        posts.MapPost("/comments", HandleCreateCommentAsync);
        posts.MapPut("/reactions/{emoji}", HandleAddReactionAsync);
        posts.MapDelete("/reactions/{emoji}", HandleRemoveReactionAsync);
        posts.MapPut("/acknowledge", HandleAcknowledgePostAsync);
        posts.MapGet("/acknowledgments", HandleGetAcknowledgmentReportAsync);
        posts.MapPost("/remind", HandleRemindOutstandingAsync);

        app.MapPut(OrganizationScope.Api + "/polls/{pollId:guid}/vote", HandleVotePollAsync);

        return app;
    }

    private static async Task<IResult> HandleListPostsAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        DateTimeOffset? before,
        string? cursor,
        Guid? aroundPostId,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }
        if (!PaginationGuard.TryDecodeCursor<PostCursor>(cursor, out var after) ||
            after is not null && (before.HasValue || aroundPostId.HasValue || after.Id == Guid.Empty || after.CreatedAt == default) ||
            aroundPostId.HasValue && (before.HasValue || aroundPostId.Value == Guid.Empty))
            return PaginationGuard.InvalidCursor();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var baseQuery = db.ChannelPosts.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.DeletedAt == null);

        List<ChannelPostEntity> posts;
        bool hasMore;
        bool hasNewer = false;

        if (aroundPostId is { } targetId)
        {
            var targetPost = await baseQuery.SingleOrDefaultAsync(p => p.Id == targetId, ct);
            if (targetPost is null)
            {
                return Results.NotFound(new { code = "post_not_found", message = "Post not found." });
            }

            var aboveBudget = pageSize / 2;
            var above = await baseQuery
                .Where(p => (!targetPost.IsPinned && p.IsPinned) ||
                    (p.IsPinned == targetPost.IsPinned &&
                     (p.CreatedAt > targetPost.CreatedAt || (p.CreatedAt == targetPost.CreatedAt && p.Id.CompareTo(targetPost.Id) > 0))))
                .OrderBy(p => p.IsPinned)
                .ThenBy(p => p.CreatedAt)
                .ThenBy(p => p.Id)
                .Take(aboveBudget + 1)
                .ToListAsync(ct);

            hasNewer = above.Count > aboveBudget;
            if (hasNewer) above = above.Take(aboveBudget).ToList();
            above.Reverse();

            var belowBudget = Math.Max(0, pageSize - above.Count - 1);
            var below = await baseQuery
                .Where(p => (targetPost.IsPinned && !p.IsPinned) ||
                    (p.IsPinned == targetPost.IsPinned &&
                     (p.CreatedAt < targetPost.CreatedAt || (p.CreatedAt == targetPost.CreatedAt && p.Id.CompareTo(targetPost.Id) < 0))))
                .OrderByDescending(p => p.IsPinned)
                .ThenByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Take(belowBudget + 1)
                .ToListAsync(ct);

            hasMore = below.Count > belowBudget;
            if (hasMore) below = below.Take(belowBudget).ToList();

            posts = [.. above, targetPost, .. below];
        }
        else
        {
            var query = baseQuery;
            if (after is not null)
            {
                var createdAt = after.CreatedAt.ToUniversalTime();
                query = query.Where(p => (after.IsPinned && !p.IsPinned) || p.IsPinned == after.IsPinned &&
                    (p.CreatedAt < createdAt || p.CreatedAt == createdAt && p.Id.CompareTo(after.Id) < 0));
            }
            else if (before.HasValue)
            {
                query = query.Where(p => !p.IsPinned && p.CreatedAt < before.Value);
            }

            posts = await query
                .OrderByDescending(p => p.IsPinned)
                .ThenByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasMore = posts.Count > pageSize;
            if (hasMore) posts = posts.Take(pageSize).ToList();
        }

        var postIds = posts.Select(p => p.Id).ToList();

        var comments = postIds.Count == 0
            ? []
            : await db.PostComments.AsNoTracking()
                .Where(c => postIds.Contains(c.PostId) && c.DeletedAt == null)
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);

        var reactions = postIds.Count == 0
            ? []
            : await db.PostReactions.AsNoTracking()
                .Where(r => postIds.Contains(r.PostId))
                .ToListAsync(ct);

        var ackPostIds = posts.Where(p => p.RequiresAcknowledgment).Select(p => p.Id).ToList();
        var acks = ackPostIds.Count == 0
            ? []
            : await db.AnnouncementAcknowledgments.AsNoTracking()
                .Where(a => ackPostIds.Contains(a.PostId))
                .ToListAsync(ct);

        var polls = postIds.Count == 0
            ? []
            : await db.Polls.AsNoTracking()
                .Where(p => postIds.Contains(p.PostId))
                .ToListAsync(ct);

        var pollIds = polls.Select(p => p.Id).ToList();
        var votes = pollIds.Count == 0
            ? []
            : await db.PollVotes.AsNoTracking()
                .Where(v => pollIds.Contains(v.PollId))
                .ToListAsync(ct);

        var authorIds = posts.Select(p => p.AuthorUserId)
            .Concat(comments.Select(c => c.AuthorUserId))
            .Distinct()
            .ToList();

        var authors = authorIds.Count == 0
            ? new Dictionary<Guid, (string DisplayName, bool IsUnverified, string? AvatarUrl)>()
            : (await db.OrganizationPeople.AsNoTracking()
                .Where(u => authorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName, u.TrustLevel, u.AvatarObjectKey })
                .ToListAsync(ct))
                .ToDictionary(
                    u => u.Id,
                    u => (DisplayName: u.DisplayName, IsUnverified: u.TrustLevel == TrustLevel.Unverified, AvatarUrl: string.IsNullOrEmpty(u.AvatarObjectKey) ? null : $"/api/organizations/{actor.OrganizationId}/people/{u.Id}/avatar"));

        var totalRecipients = ackPostIds.Count == 0
            ? 0
            : await CountEligibleRecipientsAsync(db, access.Community, ct);

        var attachments = postIds.Count == 0
            ? []
            : await db.Attachments.AsNoTracking()
                .Where(a => a.PostId != null && postIds.Contains(a.PostId.Value) && a.State == AttachmentState.Attached)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(ct);

        var commentsByPost = comments.GroupBy(c => c.PostId).ToDictionary(g => g.Key, g => g.ToList());
        var reactionsByPost = reactions.GroupBy(r => r.PostId).ToDictionary(g => g.Key, g => g.ToList());
        var acksByPost = acks.GroupBy(a => a.PostId).ToDictionary(g => g.Key, g => g.ToList());
        var pollByPost = polls.ToDictionary(p => p.PostId);
        var votesByPoll = votes.GroupBy(v => v.PollId).ToDictionary(g => g.Key, g => g.ToList());
        var attachmentsByPost = attachments.GroupBy(a => a.PostId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        var items = posts.Select(p =>
        {
            var author = authors.GetValueOrDefault(p.AuthorUserId, ("Member", false, null));
            var postComments = (commentsByPost.GetValueOrDefault(p.Id) ?? []).Select(c =>
            {
                var cAuth = authors.GetValueOrDefault(c.AuthorUserId, ("Member", false, null));
                return new
                {
                    id = c.Id,
                    postId = c.PostId,
                    authorUserId = c.AuthorUserId,
                    authorDisplayName = cAuth.DisplayName,
                    authorIsUnverified = cAuth.IsUnverified,
                    body = c.Body,
                    createdAt = c.CreatedAt
                };
            }).ToList();

            var postReactions = (reactionsByPost.GetValueOrDefault(p.Id) ?? [])
                .GroupBy(r => r.Emoji)
                .Select(g => new
                {
                    emoji = g.Key,
                    count = g.Count(),
                    reactedByMe = g.Any(r => r.UserId == actorId)
                })
                .OrderByDescending(x => x.count)
                .ToList();

            var postAcks = acksByPost.GetValueOrDefault(p.Id) ?? [];
            object? pollDto = null;
            if (pollByPost.TryGetValue(p.Id, out var poll))
            {
                var optionSpecs = JsonSerializer.Deserialize<List<PollOptionSpec>>(poll.OptionsJson, WebJson) ?? [];
                var pollVotes = votesByPoll.GetValueOrDefault(poll.Id) ?? [];
                var distinctVoters = pollVotes.Select(v => v.UserId).Distinct().Count();
                pollDto = new
                {
                    id = poll.Id,
                    question = poll.Question,
                    allowMultiple = poll.AllowMultiple,
                    closesAt = poll.ClosesAt,
                    totalVoters = distinctVoters,
                    options = optionSpecs.Select(opt => new
                    {
                        id = opt.Id,
                        label = opt.Label,
                        votes = pollVotes.Count(v => v.OptionId == opt.Id),
                        votedByMe = pollVotes.Any(v => v.OptionId == opt.Id && v.UserId == actorId)
                    }).ToList()
                };
            }

            return new
            {
                id = p.Id,
                conversationId = p.ConversationId,
                authorUserId = p.AuthorUserId,
                authorDisplayName = author.DisplayName,
                authorIsUnverified = author.IsUnverified,
                authorAvatarUrl = author.AvatarUrl,
                title = p.Title,
                body = p.Body,
                isAnnouncement = p.IsAnnouncement,
                requiresAcknowledgment = p.RequiresAcknowledgment,
                acknowledgedCount = p.RequiresAcknowledgment ? postAcks.Count : 0,
                totalRecipients = p.RequiresAcknowledgment ? totalRecipients : 0,
                acknowledgedByMe = p.RequiresAcknowledgment && postAcks.Any(a => a.UserId == actorId),
                isPinned = p.IsPinned,
                createdAt = p.CreatedAt,
                comments = postComments,
                reactions = postReactions,
                poll = pollDto,
                attachments = (attachmentsByPost.GetValueOrDefault(p.Id) ?? []).Select(FilesEndpoints.ToDto).ToList()
            };
        }).ToList();

        return Results.Ok(new
        {
            items,
            hasMore,
            hasNewer,
            nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PostCursor(posts[^1].IsPinned, posts[^1].CreatedAt, posts[^1].Id)) : null,
            canPost = authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, access.Resource),
            canManage = access.Resource.MembershipRole is ResourceRole.Owner or ResourceRole.Admin,
            isArchived = access.Conversation.ArchivedAt is not null,
            archivedAt = access.Conversation.ArchivedAt,
            channelDescription = access.Conversation.Description
        });
    }

    private static IResult ChannelArchived() =>
        Results.Conflict(new { code = "channel_archived", message = "This channel is archived and read-only." });

    private static async Task<IResult> HandleCreatePostAsync(
        HttpContext ctx,
        Guid conversationId,
        CreatePostRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        WebPushTransport push,
        IEmailDigestTransport digest,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, access.Resource with { IsArchived = false }))
        {
            return Results.Forbid();
        }

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
        }

        var isManager = access.Resource.MembershipRole is ResourceRole.Owner or ResourceRole.Admin;
        if ((request.IsAnnouncement || request.RequiresAcknowledgment) && !isManager)
        {
            return Results.Forbid();
        }

        var attachmentIds = (request.AttachmentIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (attachmentIds.Length > AttachmentProjection.MaxAttachmentsPerItem)
        {
            return Results.BadRequest(new { code = "too_many_attachments", message = $"At most {AttachmentProjection.MaxAttachmentsPerItem} attachments per post." });
        }

        var body = (request.Body ?? string.Empty).Trim();
        if ((body.Length == 0 && attachmentIds.Length == 0) || body.Length > 8000)
        {
            return Results.BadRequest(new { code = "invalid_body", message = "Post body must be 1 to 8000 characters, or include at least one attachment." });
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        if (title is not null && title.Length > 160)
        {
            return Results.BadRequest(new { code = "invalid_title", message = "Post title must be at most 160 characters." });
        }

        PollEntity? poll = null;
        List<PollOptionSpec>? pollSpecs = null;
        var now = DateTimeOffset.UtcNow;
        var post = new ChannelPostEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ConversationId = conversationId,
            AuthorUserId = actorId,
            Title = title,
            Body = body,
            IsAnnouncement = request.IsAnnouncement,
            RequiresAcknowledgment = request.IsAnnouncement && request.RequiresAcknowledgment,
            IsPinned = false,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (request.Poll is not null)
        {
            var question = (request.Poll.Question ?? string.Empty).Trim();
            var cleanOptions = (request.Poll.Options ?? [])
                .Select(o => (o ?? string.Empty).Trim())
                .Where(o => o.Length is >= 1 and <= 120)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            if (question.Length is < 1 or > 240 || cleanOptions.Count < 2)
            {
                return Results.BadRequest(new { code = "invalid_poll", message = "Poll requires a question and at least 2 distinct options." });
            }

            pollSpecs = cleanOptions.Select((label, idx) => new PollOptionSpec($"opt_{idx + 1}", label)).ToList();
            poll = new PollEntity
            {
                Id = Guid.CreateVersion7(),
                PostId = post.Id,
                Question = question,
                OptionsJson = JsonSerializer.Serialize(pollSpecs, WebJson),
                AllowMultiple = request.Poll.AllowMultiple,
                ClosesAt = request.Poll.ClosesAt,
                CreatedAt = now
            };
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        List<AttachmentEntity> pendingAttachments = [];
        if (attachmentIds.Length > 0)
        {
            pendingAttachments = await AttachmentProjection.LoadPendingForBindingAsync(
                db, conversationId, actor.OrganizationId, actorId, attachmentIds, ct);

            if (pendingAttachments.Count != attachmentIds.Length)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_attachments",
                    message = "One or more attachments are missing, quarantined, already attached, or do not belong to you in this channel."
                });
            }
        }

        db.ChannelPosts.Add(post);
        foreach (var link in SharedLinkExtractor.Extract(body))
        {
            db.SharedLinks.Add(new SharedLinkEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = actor.OrganizationId,
                ConversationId = conversationId,
                PostId = post.Id,
                SenderUserId = actorId,
                Url = link.Url,
                Host = link.Host,
                Label = link.Label,
                CreatedAt = now
            });
        }
        foreach (var att in pendingAttachments)
        {
            att.PostId = post.Id;
            att.State = AttachmentState.Attached;
            att.AttachedAt = now;
        }

        if (poll is not null)
        {
            db.Polls.Add(poll);
        }

        if (!string.IsNullOrEmpty(body))
        {
            await MentionOutbox.EnqueueAsync(db, authz, actor, access.Resource, conversationId, access.Community,
                MentionOutbox.PostKind, post.Id, body, now, push.PublicKey is not null || digest.IsEnabled, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        NexusMeters.PostCreated();

        var attachmentDtos = pendingAttachments
            .OrderBy(a => a.CreatedAt)
            .Select(FilesEndpoints.ToDto)
            .ToList();

        var totalRecipients = post.RequiresAcknowledgment
            ? await CountEligibleRecipientsAsync(db, access.Community, ct)
            : 0;

        object? pollDto = poll is null ? null : new
        {
            id = poll.Id,
            question = poll.Question,
            allowMultiple = poll.AllowMultiple,
            closesAt = poll.ClosesAt,
            totalVoters = 0,
            options = pollSpecs!.Select(opt => new { id = opt.Id, label = opt.Label, votes = 0, votedByMe = false }).ToList()
        };

        var postDto = new
        {
            id = post.Id,
            conversationId,
            authorUserId = actorId,
            authorDisplayName = actor.DisplayName,
            authorIsUnverified = actor.TrustLevel == TrustLevel.Unverified,
            authorAvatarUrl = string.IsNullOrEmpty(actor.AvatarObjectKey) ? null : $"/api/organizations/{actor.OrganizationId}/people/{actor.Id}/avatar",
            title = post.Title,
            body = post.Body,
            isAnnouncement = post.IsAnnouncement,
            requiresAcknowledgment = post.RequiresAcknowledgment,
            acknowledgedCount = 0,
            totalRecipients,
            acknowledgedByMe = false,
            isPinned = false,
            createdAt = post.CreatedAt,
            comments = Array.Empty<object>(),
            reactions = Array.Empty<object>(),
            poll = pollDto,
            attachments = attachmentDtos
        };

        await fanout.NotifyCommunityFeedChangedAsync(
            access.Community.Id,
            conversationId,
            new { type = "post_created", communityId = access.Community.Id, conversationId, post = postDto },
            ct);

        return Results.Created($"/api/organizations/{actor.OrganizationId}/posts/{post.Id}", new
        {
            id = post.Id,
            conversationId,
            title = post.Title,
            body = post.Body,
            isAnnouncement = post.IsAnnouncement,
            requiresAcknowledgment = post.RequiresAcknowledgment,
            pollId = poll?.Id,
            attachments = attachmentDtos,
            createdAt = post.CreatedAt
        });
    }

    private static async Task<IResult> HandlePinPostAsync(
        HttpContext ctx,
        Guid postId,
        PinPostRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var post = await db.ChannelPosts.SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || access.Community is null ||
            !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource) ||
            access.Resource.MembershipRole is not (ResourceRole.Owner or ResourceRole.Admin))
        {
            return Results.Forbid();
        }

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
        }

        post.IsPinned = request.IsPinned;
        post.PinnedAt = request.IsPinned ? DateTimeOffset.UtcNow : null;
        post.PinnedByUserId = request.IsPinned ? actorId : null;
        await db.SaveChangesAsync(ct);
        await fanout.NotifyCommunityFeedChangedAsync(
            access.Community.Id,
            post.ConversationId,
            new { type = "pin", postId, isPinned = post.IsPinned },
            ct);

        return Results.Ok(new { id = post.Id, isPinned = post.IsPinned });
    }

    private static Task<int> CountEligibleRecipientsAsync(NexusDbContext db, CommunityEntity community, CancellationToken ct) =>
        AnnouncementRecipients.Eligible(db, community).CountAsync(ct);

    private static string CsvEscape(string value)
    {
        // Neutralize spreadsheet formula injection (=, +, -, @) and RFC-4180 quote
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
        {
            v = "'" + v;
        }
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : v;
    }
}
