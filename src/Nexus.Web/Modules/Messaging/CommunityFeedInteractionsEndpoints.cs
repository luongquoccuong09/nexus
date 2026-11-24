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
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Notifications;

namespace Nexus.Web.Modules.Messaging;

public static partial class CommunityFeedEndpoints
{
    private static async Task<IResult> HandleCreateCommentAsync(
        HttpContext ctx,
        Guid postId,
        CreateCommentRequest request,
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

        var post = await db.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == postId && p.OrganizationId == actor.OrganizationId && p.DeletedAt == null, ct);
        if (post is null) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, post.ConversationId, ct);
        if (access is null || access.Community is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
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
        await MentionOutbox.EnqueueAsync(db, authz, actor, access.Resource, post.ConversationId, access.Community,
            MentionOutbox.CommentKind, comment.Id, body, comment.CreatedAt, push.PublicKey is not null || digest.IsEnabled, ct);
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

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
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

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
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

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
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

        var recipientsQuery = AnnouncementRecipients.Eligible(db, access.Community)
            .Select(recipient => new
            {
                recipient.Membership.Id,
                Username = recipient.Membership.UserName ?? string.Empty,
                recipient.Membership.DisplayName
            });

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

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
        }

        var now = DateTimeOffset.UtcNow;
        var outstandingCount = await AnnouncementRecipients.Outstanding(db, access.Community, postId, now).CountAsync(ct);
        if (outstandingCount > 0)
        {
            db.OutboxMessages.Add(new OutboxMessageEntity
            {
                Id = Guid.CreateVersion7(),
                Kind = AnnouncementReminderHandler.Kind,
                OrganizationId = actor.OrganizationId,
                ResourceId = postId,
                CreatedAt = now,
                AvailableAt = now
            });
        }

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
            CreatedAt = now
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

        if (access.Conversation.ArchivedAt is not null)
        {
            return ChannelArchived();
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

}
