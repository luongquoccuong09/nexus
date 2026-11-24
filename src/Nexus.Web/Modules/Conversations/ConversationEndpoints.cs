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

    public const string SelfChatDefaultTitle = "Notes to self";

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var group = app.MapGroup(prefix + "/conversations");

        group.MapGet("/", HandleListConversationsAsync);
        group.MapPost("/dm", HandleOpenDmAsync);
        if (string.Equals(prefix, OrganizationScope.PersonalApi, StringComparison.Ordinal))
        {
            group.MapPost("/self", HandleOpenSelfChatAsync)
                .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        }
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

    public static string BuildSelfKey(Guid userId) => $"self:{userId:N}";

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
        DateTimeOffset? beforeAt,
        Guid? beforeId,
        int? limit,
        Guid? conversationId,
        string? q,
        bool unreadOnly = false,
        CancellationToken ct = default)
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

        if (beforeAt.HasValue != beforeId.HasValue)
        {
            return Results.BadRequest(new { code = "invalid_cursor", message = "Both beforeAt and beforeId are required for pagination." });
        }
        if (q?.Length > 120)
        {
            return Results.BadRequest(new { code = "invalid_search_query", message = "Search text must be at most 120 characters." });
        }

        var query = db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == actorId && m.LeftAt == null)
            .Join(
                db.Conversations.AsNoTracking().Where(c =>
                    c.OrganizationId == db.ConversationOrganizationId &&
                    c.Kind != ConversationKind.Channel &&
                    (c.Kind != ConversationKind.Self || c.CreatedByUserId == actorId)),
                m => m.ConversationId,
                c => c.Id,
                (m, c) => new { Member = m, Conv = c });
        if (conversationId is { } requestedId)
        {
            query = query.Where(item => item.Conv.Id == requestedId);
        }
        var search = HrCsvImportService.NormalizeVietnamese(q ?? string.Empty);
        if (search.Length > 0)
        {
            var pattern = "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(item => item.Conv.Kind == ConversationKind.Dm
                ? db.ConversationMembers.Any(peer => peer.ConversationId == item.Conv.Id && peer.UserId != actorId && peer.LeftAt == null &&
                    db.OrganizationPeople.Any(person => person.Id == peer.UserId &&
                        (EF.Functions.ILike(NexusDbContext.ImmutableUnaccent(person.DisplayName.ToLower()), pattern, "\\") ||
                         EF.Functions.ILike(NexusDbContext.ImmutableUnaccent(person.UserName.ToLower()), pattern, "\\"))))
                : EF.Functions.ILike(NexusDbContext.ImmutableUnaccent((item.Conv.Title ??
                    (item.Conv.Kind == ConversationKind.Self ? SelfChatDefaultTitle : "Group")).ToLower()), pattern, "\\"));
        }

        if (unreadOnly)
        {
            query = query.Where(item => item.Conv.LatestMainSeq > item.Member.VisibleFromSeq &&
                (item.Conv.LatestMainSeq > item.Member.LastReadSeq ||
                    item.Conv.LatestMainSeq >= item.Member.ManualUnreadFromSeq));
        }

        if (beforeAt is { } at && beforeId is { } id)
        {
            var utc = at.ToUniversalTime();
            query = query.Where(item => item.Conv.UpdatedAt < utc ||
                (item.Conv.UpdatedAt == utc && item.Conv.Id.CompareTo(id) < 0));
        }

        var memberships = await query
            .OrderByDescending(x => x.Conv.UpdatedAt)
            .ThenByDescending(x => x.Conv.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = memberships.Count > pageSize;
        if (hasMore) memberships.RemoveAt(pageSize);

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
                .Join(
                    db.Users.AsNoTracking(),
                    m => m.UserId,
                    acct => acct.Id,
                    (m, acct) => new
                    {
                        m.ConversationId,
                        m.UserId,
                        m.DisplayName,
                        m.Username,
                        m.TrustLevel,
                        m.AvatarObjectKey,
                        acct.AvailabilityState,
                        acct.AvailabilityExpiresAt
                    })
                .ToListAsync(ct);

        var membersByConv = activeMembers.GroupBy(x => x.ConversationId).ToDictionary(g => g.Key, g => g.ToList());
        var dmIds = memberships.Where(x => x.Conv.Kind == ConversationKind.Dm).Select(x => x.Conv.Id).ToHashSet();
        var dmPeerIds = activeMembers.Where(m => m.UserId != actorId && dmIds.Contains(m.ConversationId))
            .Select(m => m.UserId).Distinct().ToArray();
        var blockedPeerIds = dmPeerIds.Length == 0
            ? []
            : await db.UserBlocks.AsNoTracking()
                .Where(b => b.OrganizationId == db.OrganizationScopeId && b.BlockerUserId == actorId && dmPeerIds.Contains(b.BlockedUserId))
                .Select(b => b.BlockedUserId)
                .ToListAsync(ct);

        var unreadByConversation = convIds.Count == 0
            ? new Dictionary<Guid, long>()
            : await (from member in db.ConversationMembers.AsNoTracking()
                     where member.UserId == actorId && member.LeftAt == null && convIds.Contains(member.ConversationId)
                     join message in db.Messages.AsNoTracking() on member.ConversationId equals message.ConversationId
                     where message.ParentMessageId == null && message.Seq > member.VisibleFromSeq &&
                           (message.Seq > member.LastReadSeq || message.Seq >= member.ManualUnreadFromSeq)
                     group message by message.ConversationId into messages
                     select new { ConversationId = messages.Key, Count = messages.LongCount() })
                .ToDictionaryAsync(x => x.ConversationId, x => x.Count, ct);

        // Batch-load the latest visible message per conversation (strictly honoring VisibleFromSeq / SEC-18).
        var latestPreviews = new Dictionary<Guid, object>();
        if (convIds.Count > 0)
        {
            var latestSeqs = db.Messages.AsNoTracking()
                .Where(m => convIds.Contains(m.ConversationId) && m.ParentMessageId == null &&
                    db.ConversationMembers.Any(cm =>
                        cm.ConversationId == m.ConversationId &&
                        cm.UserId == actorId &&
                        cm.LeftAt == null &&
                        m.Seq > cm.VisibleFromSeq))
                .GroupBy(m => m.ConversationId)
                .Select(g => new { ConversationId = g.Key, Seq = g.Max(m => m.Seq) });

            var previews = await (from latest in latestSeqs
                                  join message in db.Messages.AsNoTracking()
                                      on new { latest.ConversationId, latest.Seq } equals new { message.ConversationId, message.Seq }
                                  join sender in db.OrganizationPeople.AsNoTracking() on message.SenderId equals sender.Id
                                  select new { message.Id, message.ConversationId, message.Seq, message.Body, message.CreatedAt,
                                      message.DeletedAt, message.Version, sender.DisplayName })
                .ToListAsync(ct);
            foreach (var row in previews)
            {
                latestPreviews[row.ConversationId] = new
                {
                    bodyPreview = row.DeletedAt.HasValue ? "This message was recalled" : FormatPreview(row.Body, "[Attachment]"),
                    senderDisplayName = row.DisplayName,
                    createdAt = row.CreatedAt,
                    seq = row.Seq,
                    messageId = row.Id,
                    version = row.Version
                };
            }
        }

        var now = DateTimeOffset.UtcNow;
        var items = memberships.Select(x =>
        {
            var members = membersByConv.GetValueOrDefault(x.Conv.Id) ?? [];
            var peer = x.Conv.Kind == ConversationKind.Dm
                ? members.FirstOrDefault(m => m.UserId != actorId)
                : null;

            return new
            {
                id = x.Conv.Id,
                kind = x.Conv.Kind.ToString().ToLowerInvariant(),
                title = x.Conv.Kind == ConversationKind.Self
                    ? (x.Conv.Title ?? SelfChatDefaultTitle)
                    : x.Conv.Kind == ConversationKind.Dm
                        ? (peer?.DisplayName ?? "Direct Message")
                        : (x.Conv.Title ?? "Group"),
                role = x.Member.Role.ToString(),
                nextSeq = x.Conv.NextSeq,
                latestMainSeq = x.Conv.LatestMainSeq,
                changeSeq = x.Conv.ChangeSeq,
                lastReadSeq = x.Member.LastReadSeq,
                manualUnreadFromSeq = x.Member.ManualUnreadFromSeq,
                manualUnreadVersion = x.Member.ManualUnreadVersion,
                unreadCount = unreadByConversation.GetValueOrDefault(x.Conv.Id),
                memberCount = members.Count,
                updatedAt = x.Conv.UpdatedAt,
                lastMessagePreview = latestPreviews.GetValueOrDefault(x.Conv.Id),
                peer = peer is null ? null : new
                {
                    id = peer.UserId,
                    username = peer.Username,
                    displayName = peer.DisplayName,
                    isUnverified = peer.TrustLevel == TrustLevel.Unverified,
                    isOnline = presence.IsOnline(db.OrganizationScopeId, peer.UserId),
                    availability = AvailabilityResolver.Resolve(peer.AvailabilityState, peer.AvailabilityExpiresAt, now),
                    isBlockedByMe = blockedPeerIds.Contains(peer.UserId),
                    avatarUrl = string.IsNullOrEmpty(peer.AvatarObjectKey) ? null : $"/api/organizations/{actor.OrganizationId}/people/{peer.UserId}/avatar"
                }
            };
        }).ToList();

        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            items,
            hasMore,
            nextBefore = hasMore ? new { updatedAt = memberships[^1].Conv.UpdatedAt, id = memberships[^1].Conv.Id } : null
        });
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

        if (target is null || (db.IsPersonalScope && !await PersonalConnections.AllowedChatPeerIds(db, actorId).ContainsAsync(request.TargetUserId, ct)))
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
                latestMainSeq = existing.LatestMainSeq,
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
            latestMainSeq = conv.LatestMainSeq,
            changeSeq = conv.ChangeSeq,
            created = inserted > 0
        });
    }

    private static async Task<IResult> HandleOpenSelfChatAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        if (!db.IsPersonalScope)
        {
            return Results.Forbid();
        }

        var actor = db.OrganizationActor ?? await db.FindPersonalActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.ConversationRead,
            new ResourceContext(
                ResourceKind.SelfChat,
                Guid.Empty,
                MembershipRole: ResourceRole.Owner,
                TargetIsSelf: true));

        if (!allowed)
        {
            return Results.Forbid();
        }

        var selfKey = BuildSelfKey(actorId);
        var existing = await db.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.OrganizationId == null && c.DmKey == selfKey && c.Kind == ConversationKind.Self, ct);

        if (existing is not null)
        {
            return Results.Ok(new
            {
                id = existing.Id,
                kind = "self",
                title = existing.Title ?? SelfChatDefaultTitle,
                nextSeq = existing.NextSeq,
                latestMainSeq = existing.LatestMainSeq,
                changeSeq = existing.ChangeSeq,
                created = false
            });
        }

        var now = DateTimeOffset.UtcNow;
        var newConvId = Guid.CreateVersion7();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversations (id, organization_id, kind, community_id, is_community_main_feed, posting_policy, title, dm_key, next_seq, change_seq, min_available_change_seq, created_by_user_id, created_at, updated_at)
            VALUES ({newConvId}, {(Guid?)null}, {"Self"}, NULL, {false}, {"Members"}, {SelfChatDefaultTitle}, {selfKey}, {0L}, {1L}, {1L}, {actorId}, {now}, {now})
            ON CONFLICT (organization_id, dm_key) WHERE dm_key IS NOT NULL DO NOTHING
            """, ct);

        var conv = await db.Conversations
            .SingleAsync(c => c.OrganizationId == null && c.DmKey == selfKey && c.Kind == ConversationKind.Self, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversation_members (conversation_id, user_id, role, last_read_seq, visible_from_seq, muted, joined_at, left_at)
            VALUES ({conv.Id}, {actorId}, {"Owner"}, {0L}, {0L}, {false}, {now}, NULL)
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
                PayloadJson = JsonSerializer.Serialize(new { kind = "self", title = conv.Title ?? SelfChatDefaultTitle }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        if (inserted > 0)
        {
            await fanout.NotifyConversationChangedAsync(conv.Id, conv.ChangeSeq, ConversationKind.Self, null, null, ct);
        }

        return Results.Ok(new
        {
            id = conv.Id,
            kind = "self",
            title = conv.Title ?? SelfChatDefaultTitle,
            nextSeq = conv.NextSeq,
            latestMainSeq = conv.LatestMainSeq,
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

        var allowedContacts = PersonalConnections.AllowedChatPeerIds(db, actorId);
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
        IPresenceTracker presence,
        string? cursor,
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

        var kind = conv?.Kind.ToResourceKind() ?? ResourceKind.GroupChat;
        var targetIsSelf = conv?.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        if (conv is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: myMembership?.Role, TargetIsSelf: targetIsSelf)))
        {
            return Results.Forbid();
        }

        if (!PaginationGuard.TryDecodeCursor<PaginationGuard.NameCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty)) return PaginationGuard.InvalidCursor();
        var rows = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Join(db.OrganizationPeople.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new
            {
                userId = u.Id,
                username = u.UserName ?? string.Empty,
                displayName = u.DisplayName,
                isUnverified = u.TrustLevel == TrustLevel.Unverified,
                isActive = db.ActiveScopePeople.Any(active => active.Id == u.Id),
                role = m.Role.ToString(),
                joinedAt = m.JoinedAt
            })
            .Join(db.Users.AsNoTracking(), x => x.userId, acct => acct.Id, (x, acct) => new
            {
                x.userId,
                x.username,
                x.displayName,
                x.isUnverified,
                x.isActive,
                x.role,
                x.joinedAt,
                acct.AvailabilityState,
                acct.AvailabilityExpiresAt
            })
            .Where(x => after == null || string.Compare(x.displayName, after.Name) > 0 ||
                x.displayName == after.Name && x.userId.CompareTo(after.Id) > 0)
            .OrderBy(x => x.displayName).ThenBy(x => x.userId)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(x => new
        {
            x.userId,
            x.username,
            x.displayName,
            x.isUnverified,
            x.isActive,
            x.role,
            x.joinedAt,
            isOnline = presence.IsOnline(db.OrganizationScopeId, x.userId),
            availability = AvailabilityResolver.Resolve(x.AvailabilityState, x.AvailabilityExpiresAt, now)
        }).ToList();
        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(rows[^1].displayName, rows[^1].userId)) : null;
        return Results.Ok(new { items, nextCursor });
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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conv = await LockGroupAsync(db, conversationId, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        if (conv is null || !authz.Can(actor.ToActorContext(), ResourceAction.GroupAddMember, new ResourceContext(ResourceKind.GroupChat, actor.OrganizationId, MembershipRole: myMembership?.Role)))
        {
            return Results.Forbid();
        }

        if (db.IsPersonalScope && !await PersonalConnections.AllowedChatPeerIds(db, actorId).ContainsAsync(request.UserId, ct)) return Results.Forbid();
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
            existingMember.ManualUnreadFromSeq = null;
            existingMember.ManualUnreadVersion++;
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
        await tx.CommitAsync(ct);
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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conv = await LockGroupAsync(db, conversationId, ct);
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
        await SidebarEndpoints.RemoveSidebarItemAsync(db, targetUserId, SidebarEndpoints.ChatSurface, conversationId, ct);

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
        await tx.CommitAsync(ct);
        await fanout.NotifyConversationAccessRevokedAsync(targetUserId, conversationId, ct);
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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conv = await LockGroupAsync(db, conversationId, ct);
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
        await SidebarEndpoints.RemoveSidebarItemAsync(db, actorId, SidebarEndpoints.ChatSurface, conversationId, ct);

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
        await tx.CommitAsync(ct);
        await fanout.NotifyConversationAccessRevokedAsync(actorId, conversationId, ct);
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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conv = await LockGroupAsync(db, conversationId, ct);
        var myMembership = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMember = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == request.NewOwnerUserId && m.LeftAt == null, ct);
        var targetUser = await db.ActiveScopePeople.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.NewOwnerUserId, ct);

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

        var promoted = await db.ConversationMembers
            .Where(m => m.ConversationId == conversationId && m.UserId == request.NewOwnerUserId && m.LeftAt == null &&
                db.ActiveScopePeople.Any(active => active.Id == m.UserId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, ResourceRole.Owner), ct);
        if (promoted != 1) return Results.Conflict(new { code = "ownership_transfer_conflict" });

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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conv = await LockGroupAsync(db, conversationId, ct);
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
        await tx.CommitAsync(ct);
        await fanout.NotifyConversationChangedAsync(conversationId, conv.ChangeSeq, ct);

        return Results.Ok(new { userId = targetUserId, role = targetMember.Role.ToString(), changeSeq = conv.ChangeSeq });
    }

    internal static string FormatPreview(string? body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback;
        var collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return fallback;
        return collapsed.Length <= 120 ? collapsed : string.Concat(collapsed.AsSpan(0, 117), "…");
    }

    // Serialize role and membership changes with transfers and message sequence allocation.
    private static Task<ConversationEntity?> LockGroupAsync(NexusDbContext db, Guid id, CancellationToken ct) =>
        db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM conversations
            WHERE id = {id} AND organization_id IS NOT DISTINCT FROM {db.ConversationOrganizationId} AND kind = 'Group'
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
}
