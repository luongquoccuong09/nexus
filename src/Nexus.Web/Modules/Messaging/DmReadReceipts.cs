using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.Messaging;

public sealed record DmReadReceiptSnapshot(
    long Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PeerLastReadSeq,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PeerLastReadAt);

public sealed record DmReadReceiptPreference(bool Enabled, long Version);

internal static class DmReadReceipts
{
    internal sealed class Pair
    {
        public Guid ConversationId { get; init; }
        public Guid? OrganizationId { get; init; }
        public Guid PeerId { get; init; }
        public long Version { get; init; }
        public long? MyLastReadSeq { get; init; }
        public DateTimeOffset? MyLastReadAt { get; init; }
        public long? PeerLastReadSeq { get; init; }
        public DateTimeOffset? PeerLastReadAt { get; init; }
        internal DmReadReceiptSnapshot ForActor => new(Version, PeerLastReadSeq, PeerLastReadAt);
        internal DmReadReceiptSnapshot ForPeer => new(Version, MyLastReadSeq, MyLastReadAt);
    }

    internal static IQueryable<Pair> Pairs(NexusDbContext db, Guid actorId, bool allScopes = false)
    {
        var scopedOrganization = db.ConversationOrganizationId;
        return from mine in db.ConversationMembers.AsNoTracking()
               join conversation in db.Conversations.AsNoTracking() on mine.ConversationId equals conversation.Id
               join other in db.ConversationMembers.AsNoTracking() on conversation.Id equals other.ConversationId
               join actor in db.Users.AsNoTracking() on mine.UserId equals actor.Id
               join peer in db.Users.AsNoTracking() on other.UserId equals peer.Id
               where mine.UserId == actorId && other.UserId != actorId && mine.LeftAt == null && other.LeftAt == null &&
                   conversation.Kind == ConversationKind.Dm && (allScopes || conversation.OrganizationId == scopedOrganization) &&
                   actor.Status == AccountStatus.Active && peer.Status == AccountStatus.Active &&
                   (conversation.OrganizationId == null ||
                    (db.OrganizationMemberships.Any(member => member.OrganizationId == conversation.OrganizationId &&
                        member.Id == actorId && member.Status == AccountStatus.Active) &&
                     db.OrganizationMemberships.Any(member => member.OrganizationId == conversation.OrganizationId &&
                        member.Id == peer.Id && member.Status == AccountStatus.Active)))
               let canShare = actor.DmReadReceiptsEnabled && peer.DmReadReceiptsEnabled && !db.UserBlocks.Any(block =>
                   block.OrganizationId == (conversation.OrganizationId ?? Guid.Empty) &&
                   ((block.BlockerUserId == actorId && block.BlockedUserId == peer.Id) ||
                    (block.BlockerUserId == peer.Id && block.BlockedUserId == actorId)))
               select new Pair
               {
                   ConversationId = conversation.Id, OrganizationId = conversation.OrganizationId, PeerId = peer.Id,
                   Version = actor.DmReadReceiptsVersion + peer.DmReadReceiptsVersion,
                   MyLastReadSeq = canShare ? mine.LastReadSeq : null, MyLastReadAt = canShare ? mine.LastReadAt : null,
                   PeerLastReadSeq = canShare ? other.LastReadSeq : null, PeerLastReadAt = canShare ? other.LastReadAt : null
               };
    }

    internal static Task<Dictionary<Guid, DmReadReceiptSnapshot>> LoadAsync(
        NexusDbContext db, Guid actorId, Guid[] conversationIds, CancellationToken ct) =>
        conversationIds.Length == 0 ? Task.FromResult(new Dictionary<Guid, DmReadReceiptSnapshot>()) :
            Pairs(db, actorId).Where(pair => conversationIds.Contains(pair.ConversationId))
                .ToDictionaryAsync(pair => pair.ConversationId, pair => pair.ForActor, ct);

    internal static Task<int> BumpPrivacyVersionAsync(NexusDbContext db, Guid actorId, CancellationToken ct) =>
        db.Users.Where(actor => actor.Id == actorId).ExecuteUpdateAsync(update =>
            update.SetProperty(actor => actor.DmReadReceiptsVersion, actor => actor.DmReadReceiptsVersion + 1), ct);
}
