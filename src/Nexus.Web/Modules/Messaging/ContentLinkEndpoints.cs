using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public static class ContentLinkCodec
{
    public sealed record Payload(string K, Guid? O, Guid C, Guid? T);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(string kind, Guid? organizationId, Guid conversationId, Guid? targetId = null) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Payload(kind, organizationId, conversationId, targetId), JsonOptions));

    public static bool TryDecode(string? token, out Payload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
        {
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<Payload>(WebEncoders.Base64UrlDecode(token.Trim()), JsonOptions);
            if (decoded is null || decoded.C == Guid.Empty)
            {
                return false;
            }

            if (decoded.K is not ("m" or "p" or "c"))
            {
                return false;
            }

            if (decoded.K is "m" or "p" && (decoded.T is null || decoded.T.Value == Guid.Empty))
            {
                return false;
            }

            if (decoded.O is { } orgId && orgId == Guid.Empty)
            {
                return false;
            }

            payload = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

public static class ContentLinkEndpoints
{
    private static IResult Unavailable() =>
        Results.NotFound(new
        {
            code = "link_unavailable",
            message = "This link is unavailable or you do not have access to it."
        });

    public static IEndpointRouteBuilder MapContentLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/links/resolve", HandleResolveAsync);
        return app;
    }

    private static async Task<IResult> HandleResolveAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        string? target,
        CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        if (!ContentLinkCodec.TryDecode(target, out var payload) || payload is null)
        {
            return Unavailable();
        }

        if (payload.K == "m")
        {
            return await ResolveMessageAsync(db, authz, actorId, payload, ct);
        }

        if (payload.O is not { } orgId)
        {
            return Unavailable();
        }

        var actor = await db.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == actorId && m.OrganizationId == orgId && m.Status == AccountStatus.Active, ct);
        if (actor is null)
        {
            return Unavailable();
        }

        var channel = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == payload.C && c.OrganizationId == orgId && c.Kind == ConversationKind.Channel && c.CommunityId != null, ct);
        if (channel?.CommunityId is not { } communityId)
        {
            return Unavailable();
        }

        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == orgId, ct);
        var membership = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);
        if (community is null || membership is null)
        {
            return Unavailable();
        }

        var resource = new ResourceContext(
            ResourceKind.CommunityChannel,
            orgId,
            MembershipRole: membership.Role,
            IsOfficial: community.IsOfficial);
        if (!authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, resource))
        {
            return Unavailable();
        }

        if (payload.K == "c")
        {
            return Results.Ok(new
            {
                kind = "channel",
                organizationId = orgId,
                communityId = community.Id,
                conversationId = channel.Id
            });
        }

        var postId = payload.T!.Value;
        var postExists = await db.ChannelPosts.AsNoTracking()
            .AnyAsync(p => p.Id == postId && p.ConversationId == channel.Id && p.OrganizationId == orgId && p.DeletedAt == null, ct);
        if (!postExists)
        {
            return Unavailable();
        }

        return Results.Ok(new
        {
            kind = "post",
            organizationId = orgId,
            communityId = community.Id,
            conversationId = channel.Id,
            postId
        });
    }

    private static async Task<IResult> ResolveMessageAsync(
        NexusDbContext db,
        IAuthorizationService authz,
        Guid actorId,
        ContentLinkCodec.Payload payload,
        CancellationToken ct)
    {
        OrganizationMembership? actor;
        if (payload.O is { } orgId)
        {
            actor = await db.OrganizationMemberships.AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == actorId && m.OrganizationId == orgId && m.Status == AccountStatus.Active, ct);
        }
        else
        {
            actor = await db.FindPersonalActorAsync(actorId, ct);
        }

        if (actor is null)
        {
            return Unavailable();
        }

        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == payload.C && c.OrganizationId == payload.O && c.Kind != ConversationKind.Channel, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == payload.C && m.UserId == actorId && m.LeftAt == null, ct);
        if (conv is null || member is null)
        {
            return Unavailable();
        }

        var kind = conv.Kind.ToResourceKind();
        var targetIsSelf = conv.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        var resource = new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role, TargetIsSelf: targetIsSelf);
        if (!authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, resource))
        {
            return Unavailable();
        }

        var messageId = payload.T!.Value;
        var message = await MessageHistory.VisibleAfter(db, member.VisibleFromSeq)
            .Where(m => m.Id == messageId && m.ConversationId == conv.Id && m.DeletedAt == null)
            .Select(m => new
            {
                m.Id,
                m.Seq,
                m.ParentMessageId,
                ParentSeq = db.Messages.Where(parent => parent.Id == m.ParentMessageId)
                    .Select(parent => (long?)parent.Seq).FirstOrDefault()
            })
            .SingleOrDefaultAsync(ct);
        if (message is null)
        {
            return Unavailable();
        }

        return Results.Ok(new
        {
            kind = "message",
            organizationId = payload.O,
            conversationId = conv.Id,
            messageId = message.Id,
            seq = message.Seq,
            parentMessageId = message.ParentMessageId,
            parentSeq = message.ParentSeq
        });
    }
}
