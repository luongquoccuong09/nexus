using Nexus.Web.Modules.Org;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Messaging;

public static class CommunityFeedEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public sealed record CreatePollInput(string Question, string[] Options, bool AllowMultiple = false, DateTimeOffset? ClosesAt = null);
    public sealed record CreatePostRequest(string? Title, string Body, bool IsAnnouncement = false, bool RequiresAcknowledgment = false, CreatePollInput? Poll = null);
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

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var query = db.ChannelPosts.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.DeletedAt == null);

        if (before.HasValue)
        {
            query = query.Where(p => !p.IsPinned && p.CreatedAt < before.Value);
        }

        var posts = await query
            .OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.CreatedAt)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = posts.Count > pageSize;
        if (hasMore) posts = posts.Take(pageSize).ToList();

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

        var commentsByPost = comments.GroupBy(c => c.PostId).ToDictionary(g => g.Key, g => g.ToList());
        var reactionsByPost = reactions.GroupBy(r => r.PostId).ToDictionary(g => g.Key, g => g.ToList());
        var acksByPost = acks.GroupBy(a => a.PostId).ToDictionary(g => g.Key, g => g.ToList());
        var pollByPost = polls.ToDictionary(p => p.PostId);
        var votesByPoll = votes.GroupBy(v => v.PollId).ToDictionary(g => g.Key, g => g.ToList());

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
                poll = pollDto
            };
        }).ToList();

        return Results.Ok(new
        {
            items,
            hasMore,
            canPost = authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, access.Resource),
            canManage = access.Resource.MembershipRole is ResourceRole.Owner or ResourceRole.Admin
        });
    }

    private static async Task<IResult> HandleCreatePostAsync(
        HttpContext ctx,
        Guid conversationId,
        CreatePostRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, access.Resource))
        {
            return Results.Forbid();
        }

        var isManager = access.Resource.MembershipRole is ResourceRole.Owner or ResourceRole.Admin;
        if ((request.IsAnnouncement || request.RequiresAcknowledgment) && !isManager)
        {
            return Results.Forbid();
        }

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length is < 1 or > 8000)
        {
            return Results.BadRequest(new { code = "invalid_body", message = "Post body must be 1 to 8000 characters." });
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        if (title is not null && title.Length > 160)
        {
            return Results.BadRequest(new { code = "invalid_title", message = "Post title must be at most 160 characters." });
        }

        PollEntity? poll = null;
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

            var specs = cleanOptions.Select((label, idx) => new PollOptionSpec($"opt_{idx + 1}", label)).ToList();
            poll = new PollEntity
            {
                Id = Guid.CreateVersion7(),
                PostId = post.Id,
                Question = question,
                OptionsJson = JsonSerializer.Serialize(specs, WebJson),
                AllowMultiple = request.Poll.AllowMultiple,
                ClosesAt = request.Poll.ClosesAt,
                CreatedAt = now
            };
        }

        db.ChannelPosts.Add(post);
        if (poll is not null)
        {
            db.Polls.Add(poll);
        }

        await db.SaveChangesAsync(ct);
        await fanout.NotifyCommunityFeedChangedAsync(access.Community.Id, conversationId, ct);

        return Results.Created($"/api/organizations/{actor.OrganizationId}/posts/{post.Id}", new
        {
            id = post.Id,
            conversationId,
            title = post.Title,
            body = post.Body,
            isAnnouncement = post.IsAnnouncement,
            requiresAcknowledgment = post.RequiresAcknowledgment,
            pollId = poll?.Id,
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

    private static async Task<IResult> HandleCreateCommentAsync(
        HttpContext ctx,
        Guid postId,
        CreateCommentRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var post = await db.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length is < 1 or > 2000)
        {
            return Results.BadRequest(new { code = "invalid_comment", message = "Comment must be 1 to 2000 characters." });
        }

        var comment = new PostCommentEntity
        {
            Id = Guid.CreateVersion7(),
            PostId = postId,
            AuthorUserId = actorId,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PostComments.Add(comment);
        await db.SaveChangesAsync(ct);

        var commentDto = new
        {
            id = comment.Id,
            postId,
            authorUserId = actorId,
            authorDisplayName = actor.DisplayName,
            authorIsUnverified = actor.TrustLevel == TrustLevel.Unverified,
            body = comment.Body,
            createdAt = comment.CreatedAt
        };
        await fanout.NotifyCommunityFeedChangedAsync(
            access.Community.Id,
            post.ConversationId,
            new { type = "comment", postId, comment = commentDto },
            ct);

        return Results.Created($"/api/organizations/{actor.OrganizationId}/posts/{postId}/comments/{comment.Id}", commentDto);
    }

    private static async Task<IResult> HandleAddReactionAsync(
        HttpContext ctx,
        Guid postId,
        string emoji,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var decodedEmoji = Uri.UnescapeDataString(emoji ?? string.Empty).Trim();
        if (decodedEmoji.Length is < 1 or > 32)
        {
            return Results.BadRequest(new { code = "invalid_emoji" });
        }

        var post = await db.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO post_reactions (post_id, user_id, emoji, created_at)
            VALUES ({postId}, {actorId}, {decodedEmoji}, {now})
            ON CONFLICT (post_id, user_id, emoji) DO NOTHING
            """, ct);

        if (access.Community is not null)
        {
            await fanout.NotifyCommunityFeedChangedAsync(
                access.Community.Id,
                post.ConversationId,
                new { type = "reaction", postId, emoji = decodedEmoji, userId = actorId, reacted = true },
                ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> HandleRemoveReactionAsync(
        HttpContext ctx,
        Guid postId,
        string emoji,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var decodedEmoji = Uri.UnescapeDataString(emoji ?? string.Empty).Trim();
        var post = await db.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        await db.PostReactions
            .Where(r => r.PostId == postId && r.UserId == actorId && r.Emoji == decodedEmoji)
            .ExecuteDeleteAsync(ct);

        if (access.Community is not null)
        {
            await fanout.NotifyCommunityFeedChangedAsync(
                access.Community.Id,
                post.ConversationId,
                new { type = "reaction", postId, emoji = decodedEmoji, userId = actorId, reacted = false },
                ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> HandleAcknowledgePostAsync(
        HttpContext ctx,
        Guid postId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var post = await db.ChannelPosts.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null || !post.RequiresAcknowledgment)
        {
            return Results.NotFound(new { code = "acknowledgment_not_enabled" });
        }

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO announcement_acknowledgments (post_id, user_id, acknowledged_at)
            VALUES ({postId}, {actorId}, {now})
            ON CONFLICT (post_id, user_id) DO NOTHING
            """, ct);

        var count = await db.AnnouncementAcknowledgments.CountAsync(a => a.PostId == postId, ct);
        var total = await CountEligibleRecipientsAsync(db, access.Community, ct);

        return Results.Ok(new { postId, acknowledged = true, acknowledgedCount = count, totalRecipients = total });
    }

    private static async Task<IResult> HandleGetAcknowledgmentReportAsync(
        HttpContext ctx,
        Guid postId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        string format = "json",
        CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var post = await db.ChannelPosts.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || access.Community is null ||
            !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource) ||
            access.Resource.MembershipRole is not (ResourceRole.Owner or ResourceRole.Admin))
        {
            return Results.Forbid();
        }

        var recipientsQuery = db.CommunityMembers.AsNoTracking()
            .Where(m => m.CommunityId == access.Community.Id && m.LeftAt == null)
            .Join(
                db.OrganizationPeople.AsNoTracking().Where(u => u.Status == AccountStatus.Active),
                m => m.UserId,
                u => u.Id,
                (m, u) => new { u.Id, Username = u.UserName ?? string.Empty, u.DisplayName, u.TrustLevel });

        if (access.Community.IsOfficial)
        {
            recipientsQuery = recipientsQuery.Where(x => x.TrustLevel == TrustLevel.Verified);
        }

        var recipients = await recipientsQuery.OrderBy(x => x.DisplayName).ToListAsync(ct);
        var ackMap = await db.AnnouncementAcknowledgments.AsNoTracking()
            .Where(a => a.PostId == postId)
            .ToDictionaryAsync(a => a.UserId, a => a.AcknowledgedAt, ct);

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.AppendLine("username,display_name,status,acknowledged_at");
            foreach (var r in recipients)
            {
                var hasAck = ackMap.TryGetValue(r.Id, out var ackAt);
                sb.Append(CsvEscape(r.Username)).Append(',')
                  .Append(CsvEscape(r.DisplayName)).Append(',')
                  .Append(hasAck ? "acknowledged" : "outstanding").Append(',')
                  .AppendLine(hasAck ? ackAt.ToString("O") : string.Empty);
            }

            StorageKeyHelper.ApplySafeDownloadHeaders(ctx.Response, $"acknowledgments-{postId:N}.csv", "text/csv; charset=utf-8");
            await ctx.Response.WriteAsync(sb.ToString(), ct);
            return Results.Empty;
        }

        var acknowledged = recipients
            .Where(r => ackMap.ContainsKey(r.Id))
            .Select(r => new { userId = r.Id, username = r.Username, displayName = r.DisplayName, acknowledgedAt = ackMap[r.Id] })
            .ToList();

        var outstanding = recipients
            .Where(r => !ackMap.ContainsKey(r.Id))
            .Select(r => new { userId = r.Id, username = r.Username, displayName = r.DisplayName })
            .ToList();

        return Results.Ok(new
        {
            postId,
            totalRecipients = recipients.Count,
            acknowledgedCount = acknowledged.Count,
            outstandingCount = outstanding.Count,
            acknowledged = acknowledged.Take(50).ToList(),
            outstanding = outstanding.Take(50).ToList()
        });
    }

    private static async Task<IResult> HandleRemindOutstandingAsync(
        HttpContext ctx,
        Guid postId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var post = await db.ChannelPosts.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null || !post.RequiresAcknowledgment) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || access.Community is null ||
            !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource) ||
            access.Resource.MembershipRole is not (ResourceRole.Owner or ResourceRole.Admin))
        {
            return Results.Forbid();
        }

        var total = await CountEligibleRecipientsAsync(db, access.Community, ct);
        var acked = await db.AnnouncementAcknowledgments.CountAsync(a => a.PostId == postId, ct);
        var outstandingCount = Math.Max(0, total - acked);

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "ANNOUNCEMENT_REMINDER_SENT",
            ResourceType = "ChannelPost",
            ResourceId = postId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { outstandingCount }, WebJson),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { postId, remindedCount = outstandingCount });
    }

    private static async Task<IResult> HandleVotePollAsync(
        HttpContext ctx,
        Guid pollId,
        VotePollRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var poll = await db.Polls.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pollId, ct);
        if (poll is null) return Results.NotFound();

        if (poll.ClosesAt.HasValue && DateTimeOffset.UtcNow > poll.ClosesAt.Value)
        {
            return Results.BadRequest(new { code = "poll_closed", message = "This poll is closed." });
        }

        var post = await db.ChannelPosts.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == poll.PostId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var validOptions = (JsonSerializer.Deserialize<List<PollOptionSpec>>(poll.OptionsJson, WebJson) ?? [])
            .Select(o => o.Id)
            .ToHashSet(StringComparer.Ordinal);

        var chosen = (request.OptionIds ?? [])
            .Where(id => validOptions.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (chosen.Count == 0)
        {
            return Results.BadRequest(new { code = "invalid_option", message = "Select at least one valid option." });
        }

        if (!poll.AllowMultiple && chosen.Count > 1)
        {
            return Results.BadRequest(new { code = "single_choice_only", message = "This poll allows only one choice." });
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.PollVotes
            .Where(v => v.PollId == pollId && v.UserId == actorId)
            .ExecuteDeleteAsync(ct);

        foreach (var optId in chosen)
        {
            db.PollVotes.Add(new PollVoteEntity
            {
                PollId = pollId,
                UserId = actorId,
                OptionId = optId,
                VotedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var allVotes = await db.PollVotes.AsNoTracking().Where(v => v.PollId == pollId).ToListAsync(ct);
        return Results.Ok(new
        {
            pollId,
            totalVoters = allVotes.Select(v => v.UserId).Distinct().Count(),
            selectedOptionIds = chosen
        });
    }

    private static async Task<int> CountEligibleRecipientsAsync(NexusDbContext db, CommunityEntity community, CancellationToken ct)
    {
        var query = db.CommunityMembers.AsNoTracking()
            .Where(m => m.CommunityId == community.Id && m.LeftAt == null)
            .Join(
                db.OrganizationPeople.AsNoTracking().Where(u => u.Status == AccountStatus.Active),
                m => m.UserId,
                u => u.Id,
                (m, u) => u.TrustLevel);

        if (community.IsOfficial)
        {
            query = query.Where(tl => tl == TrustLevel.Verified);
        }

        return await query.CountAsync(ct);
    }

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
