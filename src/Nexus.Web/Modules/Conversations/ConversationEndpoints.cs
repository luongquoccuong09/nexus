using Nexus.Web.Modules.People;
using Nexus.Web.Modules.Org;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Idempotency;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.Conversations;

public static class ConversationEndpoints
{
    public sealed record OpenDmRequest(Guid TargetUserId);
    public sealed record CreateGroupRequest(string Title, Guid[]? MemberUserIds);
    public sealed record AddMemberRequest(Guid UserId);
    public sealed record TransferOwnershipRequest(Guid NewOwnerUserId);
    public sealed record SetAdminRoleRequest(bool IsAdmin);

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var group = app.MapGroup(prefix + "/conversations");

        group.MapGet("/", HandleListConversationsAsync);
        group.MapPost("/dm", HandleOpenDmAsync);
        group.MapPost("/groups", HandleCreateGroupAsync)
            .AddEndpointFilter<IdempotencyEndpointFilter>();

        group.MapGet("/{conversationId:guid}/members", HandleListMembersAsync);
        group.MapPost("/{conversationId:guid}/members", HandleAddMemberAsync);
        group.MapDelete("/{conversationId:guid}/members/{targetUserId:guid}", HandleRemoveMemberAsync);
        group.MapPost("/{conversationId:guid}/leave", HandleLeaveGroupAsync);
        group.MapPost("/{conversationId:guid}/transfer-ownership", HandleTransferOwnershipAsync);
        group.MapPut("/{conversationId:guid}/admins/{targetUserId:guid}", HandleSetAdminRoleAsync);

        return app;
    }

    public static string BuildDmKey(Guid a, Guid b)
    {
        var sa = a.ToString("N");
        var sb = b.ToString("N");
        return string.Compare(sa, sb, StringComparison.Ordinal) <= 0 ? $"{sa}:{sb}" : $"{sb}:{sa}";
    }

    private static async Task<IResult> HandleListConversationsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IPresenceTracker presence,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var memberships = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == actorId && m.LeftAt == null)
            .Join(
                db.Conversations.AsNoTracking().Where(c => c.OrganizationId == db.ConversationOrganizationId && c.Kind != ConversationKind.Channel),
                m => m.ConversationId,
                c => c.Id,
                (m, c) => new { Member = m, Conv = c })
            .OrderByDescending(x => x.Conv.UpdatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        var convIds = memberships.Select(x => x.Conv.Id).ToList();
        var activeMembers = convIds.Count == 0
            ? []
            : await db.ConversationMembers
                .AsNoTracking()
                .Where(m => convIds.Contains(m.ConversationId) && m.LeftAt == null)
                .Join(
                    db.OrganizationPeople.AsNoTracking(),
                    m => m.UserId,
                    u => u.Id,
                    (m, u) => new
                    {
                        m.ConversationId,
                        m.UserId,
                        u.DisplayName,
                        Username = u.UserName ?? string.Empty,
                        u.TrustLevel,
                        u.AvatarObjectKey
                    })
                .ToListAsync(ct);

        var membersByConv = activeMembers.GroupBy(x => x.ConversationId).ToDictionary(g => g.Key, g => g.ToList());

        var items = memberships.Select(x =>
        {
            var members = membersByConv.GetValueOrDefault(x.Conv.Id) ?? [];
            var peer = x.Conv.Kind == ConversationKind.Dm
                ? members.FirstOrDefault(m => m.UserId != actorId)
                : null;

            var unreadCount = Math.Max(0L, x.Conv.NextSeq - Math.Max(x.Member.LastReadSeq, x.Member.VisibleFromSeq));

            return new
            {
                id = x.Conv.Id,
                kind = x.Conv.Kind.ToString().ToLowerInvariant(),
                title = x.Conv.Kind == ConversationKind.Dm ? (peer?.DisplayName ?? "Direct Message") : (x.Conv.Title ?? "Group"),
                role = x.Member.Role.ToString(),
                nextSeq = x.Conv.NextSeq,
                changeSeq = x.Conv.ChangeSeq,
                lastReadSeq = x.Member.LastReadSeq,
                unreadCount,
                memberCount = members.Count,
                updatedAt = x.Conv.UpdatedAt,
                peer = peer is null ? null : new
                {
                    id = peer.UserId,
                    username = peer.Username,
                    displayName = peer.DisplayName,
                    isUnverified = peer.TrustLevel == TrustLevel.Unverified,
                    isOnline = presence.IsOnline(db.OrganizationScopeId, peer.UserId),
                    avatarUrl = string.IsNullOrEmpty(peer.AvatarObjectKey) ? null : $"/api/organizations/{actor.OrganizationId}/people/{peer.UserId}/avatar"
                }
            };
        }).ToList();

        return Results.Ok(new { items });
    }

    private static async Task<IResult> HandleOpenDmAsync(
        HttpContext ctx,
        OpenDmRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<IdentityModuleOptions> identityOptions,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var target = await db.OrganizationPeople
            .AsNoTracking()
            .Where(u => u.Id == request.TargetUserId && u.Status == AccountStatus.Active)
            .Select(u => new { u.Id, u.Status, u.DisplayName, u.TrustLevel })
            .SingleOrDefaultAsync(ct);

        if (target is null || (db.IsPersonalScope && !await PersonalConnections.AcceptedContactIds(db, actorId).ContainsAsync(request.TargetUserId, ct)))
        {
            return Results.NotFound(new { code = "user_not_found", message = "Target user not found." });
        }

        var isBlocked = await db.UserBlocks.AnyAsync(
            b => b.OrganizationId == db.OrganizationScopeId && ((b.BlockerUserId == actorId && b.BlockedUserId == target.Id) ||
                 (b.BlockerUserId == target.Id && b.BlockedUserId == actorId)),
            ct);

        var dmKey = BuildDmKey(actorId, target.Id);
        var existing = await db.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.OrganizationId == db.ConversationOrganizationId && c.DmKey == dmKey, ct);

        var dailyQuotaExceeded = false;
        if (existing is null && actor.TrustLevel == TrustLevel.Unverified)
        {
            var utcDayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var todayCount = await db.Conversations.CountAsync(
                c => c.OrganizationId == db.ConversationOrganizationId &&
                     c.Kind == ConversationKind.Dm &&
                     c.CreatedByUserId == actorId &&
                     c.CreatedAt >= utcDayStart,
                ct);
            dailyQuotaExceeded = todayCount >= identityOptions.Value.DailyNewDmLimitForUnverified;
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.DmOpen,
            new ResourceContext(
                ResourceKind.Dm,
                actor.OrganizationId,
                TargetIsActive: target.Status == AccountStatus.Active,
                IsBlocked: isBlocked,
                DailyNewDmQuotaExceeded: dailyQuotaExceeded,
                TargetIsSelf: target.Id == actorId));

        if (!allowed)
        {
            return Results.Forbid();
        }

        if (existing is not null)
        {
            return Results.Ok(new
            {
                id = existing.Id,
                kind = "dm",
                dmKey = existing.DmKey,
                nextSeq = existing.NextSeq,
                changeSeq = existing.ChangeSeq,
                created = false
            });
        }

        var now = DateTimeOffset.UtcNow;
        var newConvId = Guid.CreateVersion7();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Atomic upsert by dm_key so concurrent tabs open exactly one DM conversation (M2.2).
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversations (id, organization_id, kind, community_id, is_community_main_feed, posting_policy, title, dm_key, next_seq, change_seq, min_available_change_seq, created_by_user_id, created_at, updated_at)
            VALUES ({newConvId}, {db.ConversationOrganizationId}, {"Dm"}, NULL, {false}, {"Members"}, NULL, {dmKey}, {0L}, {1L}, {1L}, {actorId}, {now}, {now})
            ON CONFLICT (organization_id, dm_key) WHERE dm_key IS NOT NULL DO NOTHING
            """, ct);

        var conv = await db.Conversations
            .SingleAsync(c => c.OrganizationId == db.ConversationOrganizationId && c.DmKey == dmKey, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversation_members (conversation_id, user_id, role, last_read_seq, visible_from_seq, muted, joined_at, left_at)
            VALUES ({conv.Id}, {actorId}, {"Member"}, {0L}, {0L}, {false}, {now}, NULL),
                   ({conv.Id}, {target.Id}, {"Member"}, {0L}, {0L}, {false}, {now}, NULL)
            ON CONFLICT (conversation_id, user_id) DO NOTHING
            """, ct);

        if (inserted > 0)
        {
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conv.Id,
                ChangeSeq = 1,
                EventType = "conversation.created",
                ActorUserId = actorId,
                PayloadJson = JsonSerializer.Serialize(new { kind = "dm" }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        if (inserted > 0)
        {
            await fanout.NotifyConversationChangedAsync(conv.Id, conv.ChangeSeq, ct);
        }

        return Results.Ok(new
        {
            id = conv.Id,
            kind = "dm",
            dmKey = conv.DmKey,
            nextSeq = conv.NextSeq,
            changeSeq = conv.ChangeSeq,
            created = inserted > 0
        });
    }

    private static async Task<IResult> HandleCreateGroupAsync(
        HttpContext ctx,
        CreateGroupRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        if (!authz.Can(actor.ToActorContext(), ResourceAction.GroupCreate, new ResourceContext(ResourceKind.GroupChat, actor.OrganizationId)))
        {
            return Results.Forbid();
        }

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length is < 1 or > 120)
        {
            return Results.BadRequest(new { code = "invalid_title", message = "Group title must be 1 to 120 characters." });
        }

        var allowedContacts = PersonalConnections.AcceptedContactIds(db, actorId);
        var requestedIds = (request.MemberUserIds ?? [])
            .Where(id => id != actorId)
            .Distinct()
            .Take(200)
            .ToList();

        var validMemberIds = requestedIds.Count == 0
            ? []
            : await db.ActiveScopePeople
                .AsNoTracking()
                .Where(u => requestedIds.Contains(u.Id) && (!db.IsPersonalScope || allowedContacts.Contains(u.Id)))
                .Select(u => u.Id)
                .ToListAsync(ct);

        if (db.IsPersonalScope && validMemberIds.Count != requestedIds.Count)
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        var conv = new ConversationEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = db.ConversationOrganizationId,
            Kind = ConversationKind.Group,
            Title = title,
            NextSeq = 0,
            ChangeSeq = 1,
            MinAvailableChangeSeq = 1,
            CreatedByUserId = actorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Conversations.Add(conv);
        db.ConversationMembers.Add(new ConversationMemberEntity
        {
            ConversationId = conv.Id,
            UserId = actorId,
            Role = ResourceRole.Owner,
            JoinedAt = now
        });

        foreach (var memberId in validMemberIds)
        {
            db.ConversationMembers.Add(new ConversationMemberEntity
            {
                ConversationId = conv.Id,
                UserId = memberId,
                Role = ResourceRole.Member,
                JoinedAt = now
            });
        }

        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conv.Id,
            ChangeSeq = 1,
            EventType = "conversation.created",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(new { kind = "group", title }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await fanout.NotifyConversationChangedAsync(conv.Id, conv.ChangeSeq, ct);

        return Results.Created($"{OrganizationScope.ApiPath(db.ConversationOrganizationId)}/conversations/{conv.Id}", new
        {
            id = conv.Id,
            kind = "group",
            title = conv.Title,
            changeSeq = conv.ChangeSeq,
            memberCount = validMemberIds.Count + 1
        });
    }

    private static async Task<IResult> HandleListMembersAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });

        var conv = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        if (conv is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: myMembership?.Role)))
        {
            return Results.Forbid();
        }

        var items = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Join(db.OrganizationPeople.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new
            {
                userId = u.Id,
                username = u.UserName ?? string.Empty,
                displayName = u.DisplayName,
                isUnverified = u.TrustLevel == TrustLevel.Unverified,
                role = m.Role.ToString(),
                joinedAt = m.JoinedAt
            })
            .OrderBy(x => x.displayName)
            .Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    private static async Task<IResult> HandleAddMemberAsync(
        HttpContext ctx,
        Guid conversationId,
        AddMemberRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId && c.Kind == ConversationKind.Group, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        if (conv is null || !authz.Can(actor.ToActorContext(), ResourceAction.GroupAddMember, new ResourceContext(ResourceKind.GroupChat, actor.OrganizationId, MembershipRole: myMembership?.Role)))
        {
            return Results.Forbid();
        }

        if (db.IsPersonalScope && !await PersonalConnections.AcceptedContactIds(db, actorId).ContainsAsync(request.UserId, ct)) return Results.Forbid();
        var target = await db.OrganizationPeople.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.UserId && u.OrganizationId == actor.OrganizationId && u.Status == AccountStatus.Active, ct);
        if (target is null)
        {
            return Results.NotFound(new { code = "user_not_found", message = "Active target user not found." });
        }

        var now = DateTimeOffset.UtcNow;
        var existingMember = await db.ConversationMembers.SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == target.Id, ct);

        if (existingMember is not null && existingMember.LeftAt is null)
        {
            return Results.Ok(new { added = false, alreadyMember = true });
        }

        conv.ChangeSeq += 1;
        conv.UpdatedAt = now;

        if (existingMember is null)
        {
            db.ConversationMembers.Add(new ConversationMemberEntity
            {
                ConversationId = conversationId,
                UserId = target.Id,
                Role = ResourceRole.Member,
                VisibleFromSeq = conv.NextSeq,
                LastReadSeq = conv.NextSeq,
                JoinedAt = now
            });
        }
        else
        {
            // Rejoining a private group hides prior history per SYNC_PROTOCOL.md §3.
            existingMember.LeftAt = null;
            existingMember.Role = ResourceRole.Member;
            existingMember.VisibleFromSeq = conv.NextSeq;
            existingMember.LastReadSeq = conv.NextSeq;
            existingMember.JoinedAt = now;
        }

        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = conv.ChangeSeq,
            EventType = "member.added",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(new { userId = target.Id }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await fanout.NotifyConversationChangedAsync(conversationId, conv.ChangeSeq, ct);

        return Results.Ok(new { added = true, visibleFromSeq = conv.NextSeq, changeSeq = conv.ChangeSeq });
    }

    private static async Task<IResult> HandleRemoveMemberAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId && c.Kind == ConversationKind.Group, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMembership = await db.ConversationMembers.SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == targetUserId && m.LeftAt == null, ct);

        if (conv is null || targetMembership is null)
        {
            return Results.Forbid();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.GroupRemoveMember,
            new ResourceContext(
                ResourceKind.GroupChat,
                actor.OrganizationId,
                MembershipRole: myMembership?.Role,
                TargetIsSelf: targetUserId == actorId,
                TargetIsOwner: targetMembership.Role == ResourceRole.Owner));

        if (!allowed)
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        targetMembership.LeftAt = now;
        conv.ChangeSeq += 1;
        conv.UpdatedAt = now;

        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = conv.ChangeSeq,
            EventType = "member.removed",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(new { userId = targetUserId }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await fanout.NotifyConversationChangedAsync(conversationId, conv.ChangeSeq, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleLeaveGroupAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId && c.Kind == ConversationKind.Group, ct);
        var myMembership = await db.ConversationMembers.SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);
        if (conv is null || myMembership is null) return Results.Forbid();

        var hasOthers = await db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.UserId != actorId && m.LeftAt == null, ct);
        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.GroupLeave,
            new ResourceContext(ResourceKind.GroupChat, actor.OrganizationId, MembershipRole: myMembership.Role, HasOtherMembers: hasOthers));

        if (!allowed)
        {
            return Results.BadRequest(new { code = "transfer_ownership_required", message = "Transfer ownership before leaving a group with other members." });
        }

        var now = DateTimeOffset.UtcNow;
        myMembership.LeftAt = now;
        conv.ChangeSeq += 1;
        conv.UpdatedAt = now;

        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = conv.ChangeSeq,
            EventType = "member.left",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(new { userId = actorId }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await fanout.NotifyConversationChangedAsync(conversationId, conv.ChangeSeq, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleTransferOwnershipAsync(
        HttpContext ctx,
        Guid conversationId,
        TransferOwnershipRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId && c.Kind == ConversationKind.Group, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMember = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == request.NewOwnerUserId && m.LeftAt == null, ct);
        var targetUser = await db.OrganizationPeople.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.NewOwnerUserId && u.OrganizationId == actor.OrganizationId, ct);

        if (conv is null || myMembership is null || targetMember is null || targetUser is null)
        {
            return Results.Forbid();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.GroupTransferOwnership,
            new ResourceContext(
                ResourceKind.GroupChat,
                actor.OrganizationId,
                MembershipRole: myMembership.Role,
                TargetIsSelf: request.NewOwnerUserId == actorId,
                TargetIsActive: targetUser.Status == AccountStatus.Active));

        if (!allowed)
        {
            return Results.Forbid();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var transition = await ConditionalTransition.ExecuteAsync(
            token => db.ConversationMembers
                .Where(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null && m.Role == ResourceRole.Owner)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, ResourceRole.Admin), token),
            async token =>
            {
                var cur = await db.ConversationMembers.AsNoTracking()
                    .Where(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null)
                    .Select(m => new { m.Role })
                    .SingleOrDefaultAsync(token);
                return cur is null ? (false, ResourceRole.Member) : (true, cur.Role);
            },
            targetState: ResourceRole.Admin,
            ct);

        if (transition.Outcome != TransitionOutcome.Succeeded)
        {
            return Results.Conflict(new { code = "ownership_transfer_conflict" });
        }

        await db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == request.NewOwnerUserId && m.LeftAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, ResourceRole.Owner), ct);

        var now = DateTimeOffset.UtcNow;
        conv.ChangeSeq += 1;
        conv.UpdatedAt = now;
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = conv.ChangeSeq,
            EventType = "owner.transferred",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(new { previousOwnerId = actorId, newOwnerId = request.NewOwnerUserId }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await fanout.NotifyConversationChangedAsync(conversationId, conv.ChangeSeq, ct);

        return Results.Ok(new { newOwnerUserId = request.NewOwnerUserId, changeSeq = conv.ChangeSeq });
    }

    private static async Task<IResult> HandleSetAdminRoleAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid targetUserId,
        SetAdminRoleRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId && c.Kind == ConversationKind.Group, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMember = await db.ConversationMembers.SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == targetUserId && m.LeftAt == null, ct);

        if (conv is null || targetMember is null || targetMember.Role == ResourceRole.Owner)
        {
            return Results.Forbid();
        }

        if (!authz.Can(actor.ToActorContext(), ResourceAction.GroupManageAdmins, new ResourceContext(ResourceKind.GroupChat, actor.OrganizationId, MembershipRole: myMembership?.Role)))
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        targetMember.Role = request.IsAdmin ? ResourceRole.Admin : ResourceRole.Member;
        conv.ChangeSeq += 1;
        conv.UpdatedAt = now;

        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = conv.ChangeSeq,
            EventType = "member.role_changed",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(new { userId = targetUserId, role = targetMember.Role.ToString() }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await fanout.NotifyConversationChangedAsync(conversationId, conv.ChangeSeq, ct);

        return Results.Ok(new { userId = targetUserId, role = targetMember.Role.ToString(), changeSeq = conv.ChangeSeq });
    }
}
