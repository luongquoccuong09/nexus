using Nexus.Web.Modules.Org;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Idempotency;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Meetings;

namespace Nexus.Web.Modules.Conversations;

public static partial class CommunityEndpoints
{
    private static async Task<IResult> HandleCreateChannelAsync(
        HttpContext ctx,
        Guid communityId,
        CreateChannelRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.ChannelCreate,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 120)
        {
            return Results.BadRequest(new { code = "invalid_channel_name", message = "Channel name must be 1 to 120 characters." });
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is not null && description.Length > 500)
        {
            return Results.BadRequest(new { code = "invalid_description", message = "Description must be at most 500 characters." });
        }

        var postingPolicy = string.Equals(request.PostingPolicy, "AdminsOnly", StringComparison.OrdinalIgnoreCase)
            ? PostingPolicy.AdminsOnly
            : PostingPolicy.Members;

        var now = DateTimeOffset.UtcNow;
        var channel = new ConversationEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            Kind = ConversationKind.Channel,
            CommunityId = communityId,
            IsCommunityMainFeed = false,
            PostingPolicy = postingPolicy,
            Title = name,
            Description = description,
            NextSeq = 0,
            ChangeSeq = 1,
            MinAvailableChangeSeq = 1,
            CreatedByUserId = actorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Conversations.Add(channel);
        await db.SaveChangesAsync(ct);
        await fanout.NotifyCommunityFeedChangedAsync(communityId, channel.Id, ct);

        return Results.Created($"/api/organizations/{actor.OrganizationId}/channels/{channel.Id}/posts", new
        {
            id = channel.Id,
            communityId,
            name = channel.Title,
            description = channel.Description,
            postingPolicy = channel.PostingPolicy.ToString(),
            isArchived = false,
            archivedAt = (DateTimeOffset?)null,
            createdAt = channel.CreatedAt
        });
    }

    private static async Task<IResult> HandleUpdateChannelAsync(
        HttpContext ctx,
        Guid communityId,
        Guid channelId,
        UpdateChannelRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 120)
        {
            return Results.BadRequest(new { code = "invalid_channel_name", message = "Channel name must be 1 to 120 characters." });
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is not null && description.Length > 500)
        {
            return Results.BadRequest(new { code = "invalid_description", message = "Description must be at most 500 characters." });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.CommunityManageSettings,
                new ResourceContext(ResourceKind.CommunityChannel, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        var channel = await LockChannelAsync(db, communityId, channelId, actor.OrganizationId, ct);
        if (channel is null)
        {
            return Results.NotFound(new { code = "channel_not_found", message = "Channel not found." });
        }

        if (channel.IsCommunityMainFeed)
        {
            return Results.Conflict(new { code = "main_feed_managed_by_community", message = "Edit the community settings to rename the main feed." });
        }

        if (channel.ArchivedAt is not null)
        {
            return Results.Conflict(new { code = "channel_archived", message = "This channel is archived and read-only." });
        }

        var now = DateTimeOffset.UtcNow;
        channel.Title = name;
        channel.Description = description;
        channel.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "CHANNEL_SETTINGS_UPDATED",
            ResourceType = "Channel",
            ResourceId = channelId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { communityId, name }, AuditJsonOptions),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await fanout.NotifyCommunityFeedChangedAsync(
            communityId,
            channel.Id,
            new { type = "channel_updated", communityId, channelId = channel.Id, name = channel.Title, description = channel.Description, updatedAt = now },
            ct);

        return Results.Ok(new
        {
            id = channel.Id,
            communityId,
            name = channel.Title,
            description = channel.Description,
            postingPolicy = channel.PostingPolicy.ToString(),
            isArchived = false,
            archivedAt = (DateTimeOffset?)null,
            updatedAt = channel.UpdatedAt
        });
    }

    private static async Task<IResult> HandleArchiveChannelAsync(
        HttpContext ctx,
        Guid communityId,
        Guid channelId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.CommunityManageSettings,
                new ResourceContext(ResourceKind.CommunityChannel, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        var channel = await LockChannelAsync(db, communityId, channelId, actor.OrganizationId, ct);
        if (channel is null)
        {
            return Results.NotFound(new { code = "channel_not_found", message = "Channel not found." });
        }

        if (channel.IsCommunityMainFeed)
        {
            return Results.Conflict(new { code = "cannot_archive_main_feed", message = "The community main feed cannot be archived." });
        }

        if (channel.ArchivedAt is not null)
        {
            return Results.Conflict(new { code = "channel_already_archived", message = "This channel is already archived." });
        }

        var now = DateTimeOffset.UtcNow;
        channel.ArchivedAt = now;
        channel.ArchivedByUserId = actorId;
        channel.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "CHANNEL_ARCHIVED",
            ResourceType = "Channel",
            ResourceId = channelId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { communityId }, AuditJsonOptions),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await ctx.RequestServices.GetRequiredService<MeetingRevocations>().RecheckConversationAsync(channel.Id, ct);
        await fanout.NotifyCommunityFeedChangedAsync(
            communityId,
            channel.Id,
            new { type = "channel_archived", communityId, channelId = channel.Id, archivedAt = now, archivedByUserId = actorId },
            ct);

        return Results.Ok(new
        {
            id = channel.Id,
            communityId,
            isArchived = true,
            archivedAt = channel.ArchivedAt,
            archivedByUserId = channel.ArchivedByUserId
        });
    }

    private static async Task<IResult> HandleRestoreChannelAsync(
        HttpContext ctx,
        Guid communityId,
        Guid channelId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.CommunityManageSettings,
                new ResourceContext(ResourceKind.CommunityChannel, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        var channel = await LockChannelAsync(db, communityId, channelId, actor.OrganizationId, ct);
        if (channel is null)
        {
            return Results.NotFound(new { code = "channel_not_found", message = "Channel not found." });
        }

        if (channel.IsCommunityMainFeed)
        {
            return Results.Conflict(new { code = "cannot_archive_main_feed", message = "The community main feed cannot be archived." });
        }

        if (channel.ArchivedAt is null)
        {
            return Results.Conflict(new { code = "channel_not_archived", message = "This channel is not archived." });
        }

        var now = DateTimeOffset.UtcNow;
        channel.ArchivedAt = null;
        channel.ArchivedByUserId = null;
        channel.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "CHANNEL_RESTORED",
            ResourceType = "Channel",
            ResourceId = channelId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { communityId }, AuditJsonOptions),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await fanout.NotifyCommunityFeedChangedAsync(
            communityId,
            channel.Id,
            new { type = "channel_restored", communityId, channelId = channel.Id, restoredAt = now, restoredByUserId = actorId },
            ct);

        return Results.Ok(new
        {
            id = channel.Id,
            communityId,
            isArchived = false,
            archivedAt = (DateTimeOffset?)null,
            archivedByUserId = (Guid?)null
        });
    }

    private static Task<ConversationEntity?> LockChannelAsync(NexusDbContext db, Guid communityId, Guid channelId, Guid organizationId, CancellationToken ct) =>
        db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM conversations
            WHERE id = {channelId} AND community_id = {communityId} AND organization_id = {organizationId} AND kind = 'Channel'
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

}
