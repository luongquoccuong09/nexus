using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.People;

public sealed class PersonalConnection
{
    public Guid Id { get; set; }
    public Guid FirstUserId { get; set; }
    public Guid SecondUserId { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? DeclinedAt { get; set; }
}

public sealed class PersonalContactLink
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

internal sealed class PersonalConnectionConfiguration : IEntityTypeConfiguration<PersonalConnection>
{
    public void Configure(EntityTypeBuilder<PersonalConnection> entity)
    {
        entity.ToTable("personal_connections");
        entity.HasKey(c => c.Id);
        entity.HasIndex(c => new { c.FirstUserId, c.SecondUserId }).IsUnique();
        entity.HasIndex(c => c.SecondUserId);
        entity.HasOne<NexusUser>().WithMany().HasForeignKey(c => c.FirstUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<NexusUser>().WithMany().HasForeignKey(c => c.SecondUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PersonalContactLinkConfiguration : IEntityTypeConfiguration<PersonalContactLink>
{
    public void Configure(EntityTypeBuilder<PersonalContactLink> entity)
    {
        entity.ToTable("personal_contact_links");
        entity.HasKey(l => l.Id);
        entity.Property(l => l.TokenHash).HasMaxLength(64);
        entity.HasIndex(l => l.TokenHash).IsUnique();
        entity.HasOne<NexusUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public static class PersonalConnections
{
    public sealed record ContactRequest(string Username);
    public sealed record ClaimContactLink(string Token);
    public sealed record ContactResponse(bool Accept);

    public static void MapPersonalConnections(this IEndpointRouteBuilder app)
    {
        var endpoints = app.MapGroup(OrganizationScope.PersonalApi);
        endpoints.MapGet("/people/directory", ListDirectoryAsync);
        endpoints.MapGet("/people/chat-candidates", ListChatCandidatesAsync);
        endpoints.MapGet("/contacts/requests", ListRequestsAsync);
        endpoints.MapPost("/contacts/requests", RequestAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapPut("/contacts/requests/{id:guid}", RespondAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapPost("/contacts/links", CreateLinkAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapPost("/contacts/links/redeem", ClaimLinkAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
    }

    public static IQueryable<Guid> AcceptedContactIds([FromServices] NexusDbContext db, Guid userId) =>
        db.PersonalConnections.Where(c => c.AcceptedAt != null && (c.FirstUserId == userId || c.SecondUserId == userId))
            .Select(c => c.FirstUserId == userId ? c.SecondUserId : c.FirstUserId)
            .Where(peerId => !db.UserBlocks.Any(b => b.OrganizationId == Guid.Empty &&
                ((b.BlockerUserId == userId && b.BlockedUserId == peerId) || (b.BlockerUserId == peerId && b.BlockedUserId == userId))));

    public static IQueryable<Guid> SharedWorkspacePeerIds([FromServices] NexusDbContext db, Guid userId) =>
        db.OrganizationMemberships
            .Where(mine => mine.Id == userId && mine.Status == AccountStatus.Active)
            .Join(
                db.OrganizationMemberships.Where(peer => peer.Id != userId && peer.Status == AccountStatus.Active),
                mine => mine.OrganizationId,
                peer => peer.OrganizationId,
                (_, peer) => peer.Id)
            .Distinct()
            .Where(peerId => !db.UserBlocks.Any(b => b.OrganizationId == Guid.Empty &&
                ((b.BlockerUserId == userId && b.BlockedUserId == peerId) || (b.BlockerUserId == peerId && b.BlockedUserId == userId))));

    public static IQueryable<Guid> AllowedChatPeerIds([FromServices] NexusDbContext db, Guid userId) =>
        AcceptedContactIds(db, userId).Union(SharedWorkspacePeerIds(db, userId));

    private static async Task<IResult> ListChatCandidatesAsync(
        string? q, string? cursor, Guid? excludeConversationId, int? limit,
        [FromServices] NexusDbContext db, IAuthorizationService authz, IPresenceTracker presence, CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error)) return Results.BadRequest(new { code = "invalid_page_size", message = error });
        if (!PaginationGuard.TryDecodeCursor<PaginationGuard.NameCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty)) return PaginationGuard.InvalidCursor();
        var actor = db.OrganizationActor!;
        var userId = actor.Id;
        var allowedIds = AllowedChatPeerIds(db, userId);
        var people = db.Users.AsNoTracking().Where(u => allowedIds.Contains(u.Id) && u.Status == AccountStatus.Active);
        if (excludeConversationId is { } conversationId)
        {
            var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
            if (access is null || access.Conversation.Kind != ConversationKind.Group ||
                !authz.Can(actor.ToActorContext(), ResourceAction.GroupAddMember, access.Resource)) return Results.Forbid();
            people = people.Where(u => !db.ConversationMembers.Any(m =>
                m.ConversationId == conversationId && m.UserId == u.Id && m.LeftAt == null));
        }
        var search = q?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            people = people.Where(u => EF.Functions.ILike(u.DisplayName, pattern, "\\") || EF.Functions.ILike(u.UserName ?? string.Empty, pattern, "\\"));
        }
        if (after is not null)
        {
            people = people.Where(u => string.Compare(u.DisplayName, after.Name) > 0 ||
                u.DisplayName == after.Name && u.Id.CompareTo(after.Id) > 0);
        }

        var rows = await people
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(pageSize + 1)
            .Select(u => new
            {
                u.Id,
                Username = u.UserName ?? string.Empty,
                u.DisplayName,
                u.AvailabilityState,
                u.AvailabilityExpiresAt
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return Results.Ok(new { items = Array.Empty<object>(), nextCursor = (string?)null });
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);

        var rowIds = rows.Select(r => r.Id).ToList();
        var myOrgIds = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.Id == userId && m.Status == AccountStatus.Active)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

        var sharedOrgRows = myOrgIds.Count == 0
            ? []
            : await db.OrganizationMemberships.AsNoTracking()
                .Where(m => myOrgIds.Contains(m.OrganizationId) && rowIds.Contains(m.Id) && m.Status == AccountStatus.Active)
                .Select(m => new { m.Id, OrgName = m.Organization.Name, m.OrganizationId, m.AvatarObjectKey })
                .ToListAsync(ct);

        var contactSet = (await AcceptedContactIds(db, userId).Where(id => rowIds.Contains(id)).ToListAsync(ct)).ToHashSet();
        var orgsByUser = sharedOrgRows.GroupBy(x => x.Id).ToDictionary(
            g => g.Key,
            g => new
            {
                Names = g.Select(x => x.OrgName).Distinct().OrderBy(n => n).ToArray(),
                AvatarUrl = g.Where(x => !string.IsNullOrEmpty(x.AvatarObjectKey))
                    .Select(x => $"/api/organizations/{x.OrganizationId}/people/{x.Id}/avatar")
                    .FirstOrDefault()
            });

        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(r =>
        {
            orgsByUser.TryGetValue(r.Id, out var orgInfo);
            return new
            {
                id = r.Id,
                username = r.Username,
                displayName = r.DisplayName,
                avatarUrl = orgInfo?.AvatarUrl,
                isOnline = presence.IsOnline(r.Id),
                availability = AvailabilityResolver.Resolve(r.AvailabilityState, r.AvailabilityExpiresAt, now),
                isContact = contactSet.Contains(r.Id),
                workspaceNames = orgInfo?.Names ?? Array.Empty<string>()
            };
        }).ToList();

        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(rows[^1].DisplayName, rows[^1].Id)) : null;
        return Results.Ok(new { items, nextCursor });
    }

    private static async Task<IResult> ListDirectoryAsync(string? q, [FromServices] NexusDbContext db, IPresenceTracker presence, Guid? after, int? limit, CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error)) return Results.BadRequest(new { code = "invalid_page_size", message = error });
        var userId = db.OrganizationActor!.Id;
        var contacts = AcceptedContactIds(db, userId);
        var people = db.Users.AsNoTracking().Where(u => contacts.Contains(u.Id) && u.Status == AccountStatus.Active);
        var search = q?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            people = people.Where(u => EF.Functions.ILike(u.DisplayName, pattern) || EF.Functions.ILike(u.UserName ?? string.Empty, pattern));
        }
        var rows = await people.Where(u => after == null || u.Id.CompareTo(after.Value) > 0).OrderBy(u => u.Id).Take(pageSize + 1)
            .Select(u => new
            {
                u.Id,
                username = u.UserName,
                u.DisplayName,
                u.AvailabilityState,
                u.AvailabilityExpiresAt
            }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(u => new
        {
            u.Id,
            u.username,
            u.DisplayName,
            isUnverified = true,
            isOnline = presence.IsOnline(u.Id),
            availability = AvailabilityResolver.Resolve(u.AvailabilityState, u.AvailabilityExpiresAt, now)
        }).ToList();
        return PaginationGuard.CursorPage(items, pageSize, item => item.Id);
    }

    private static async Task<IResult> ListRequestsAsync([FromServices] NexusDbContext db, Guid? after, int? limit, CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error)) return Results.BadRequest(new { code = "invalid_page_size", message = error });
        var userId = db.OrganizationActor!.Id;
        var items = await db.PersonalConnections.AsNoTracking()
            .Where(c => c.AcceptedAt == null && c.DeclinedAt == null && c.RequestedBy != userId && (c.FirstUserId == userId || c.SecondUserId == userId))
            .Join(db.Users.Where(u => u.Status == AccountStatus.Active), c => c.RequestedBy, u => u.Id,
                (c, u) => new { c.Id, u.DisplayName, username = u.UserName })
            .Where(c => after == null || c.Id.CompareTo(after.Value) > 0)
            .OrderBy(c => c.Id).Take(pageSize + 1).ToListAsync(ct);
        return PaginationGuard.CursorPage(items, pageSize, item => item.Id);
    }

    private static async Task<IResult> RequestAsync(ContactRequest request, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        var username = request.Username?.Trim().ToUpperInvariant() ?? string.Empty;
        if (username.Length > 256) return Results.BadRequest();
        var targetId = await db.Users.Where(u => u.NormalizedUserName == username && u.Status == AccountStatus.Active)
            .Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct);
        if (targetId is Guid id) await RecordRequestAsync(db, db.OrganizationActor!.Id, id, ct);
        return Results.Accepted();
    }

    private static async Task<IResult> RespondAsync(Guid id, ContactResponse response, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        var userId = db.OrganizationActor!.Id;
        var now = DateTimeOffset.UtcNow;
        var affected = await db.PersonalConnections.Where(c => c.Id == id && c.AcceptedAt == null && c.DeclinedAt == null &&
                c.RequestedBy != userId && (c.FirstUserId == userId || c.SecondUserId == userId) &&
                db.Users.Any(account => account.Id == c.RequestedBy && account.Status == AccountStatus.Active) &&
                !db.UserBlocks.Any(b => b.OrganizationId == Guid.Empty &&
                    ((b.BlockerUserId == userId && b.BlockedUserId == c.RequestedBy) || (b.BlockerUserId == c.RequestedBy && b.BlockedUserId == userId))))
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.AcceptedAt, response.Accept ? now : (DateTimeOffset?)null)
                .SetProperty(c => c.DeclinedAt, response.Accept ? (DateTimeOffset?)null : now), ct);
        return affected == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> CreateLinkAsync(HttpContext ctx, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.PersonalContactLinks.Add(new PersonalContactLink
        {
            Id = Guid.CreateVersion7(), UserId = db.OrganizationActor!.Id,
            TokenHash = Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync(ct);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { token });
    }

    private static async Task<IResult> ClaimLinkAsync(ClaimContactLink request, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (request.Token is not { Length: 64 } || !request.Token.All(Uri.IsHexDigit)) return Results.NotFound();
        var hash = Hash(request.Token);
        var userId = db.OrganizationActor!.Id;
        var now = DateTimeOffset.UtcNow;
        var unused = db.PersonalContactLinks.Where(l => l.TokenHash == hash && l.UserId != userId && l.UsedAt == null && l.ExpiresAt > now);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var issuer = await unused.Select(l => (Guid?)l.UserId).SingleOrDefaultAsync(ct);
        if (issuer is null || await unused.ExecuteUpdateAsync(set => set.SetProperty(l => l.UsedAt, now), ct) == 0)
            return Results.NotFound();
        await RecordRequestAsync(db, userId, issuer.Value, ct);
        await transaction.CommitAsync(ct);
        return Results.Accepted();
    }

    private static async Task RecordRequestAsync([FromServices] NexusDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        if (from == to || await db.UserBlocks.AnyAsync(b => b.OrganizationId == Guid.Empty &&
            ((b.BlockerUserId == from && b.BlockedUserId == to) || (b.BlockerUserId == to && b.BlockedUserId == from)), ct)) return;
        var (first, second) = from.CompareTo(to) < 0 ? (from, to) : (to, from);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO personal_connections ("Id", "FirstUserId", "SecondUserId", "RequestedBy")
            VALUES ({Guid.CreateVersion7()}, {first}, {second}, {from})
            ON CONFLICT ("FirstUserId", "SecondUserId") DO NOTHING
            """, ct);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
