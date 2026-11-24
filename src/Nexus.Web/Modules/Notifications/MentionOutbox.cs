using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Notifications;

internal static partial class MentionOutbox
{
    public const string MessageKind = "mention.message";
    public const string PostKind = "mention.post";
    public const string CommentKind = "mention.comment";
    public const string MessageActivity = "activity.message";
    public const string PostActivity = "activity.post";
    public const string CommentActivity = "activity.comment";

    [GeneratedRegex("""```[\s\S]*?```|`[^`\r\n]+`|\bhttps?://[^\s<>"')]+|(?<![\p{L}\p{N}._@/\\-])@(?<name>[a-z0-9_-][a-z0-9._-]{1,30}[a-z0-9_-])(?![a-z0-9_-]|\.[a-z0-9._-])""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    public static async Task EnqueueAsync(
        NexusDbContext db,
        IAuthorizationService authz,
        OrganizationMembership sender,
        ResourceContext resource,
        Guid conversationId,
        CommunityEntity? community,
        string eventKind,
        Guid resourceId,
        string body,
        DateTimeOffset now,
        bool includeOrdinaryActivity,
        CancellationToken ct)
    {
        var names = MentionPattern().Matches(body)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0 && !includeOrdinaryActivity)
        {
            return;
        }

        var hasBroadcast = names.Contains("all") || names.Contains("channel");
        names.Remove("all");
        names.Remove("channel");
        var broadcast = hasBroadcast && authz.Can(sender.ToActorContext(), ResourceAction.MentionChannelOrAll, resource);
        if (!broadcast && names.Count == 0 && !includeOrdinaryActivity)
        {
            return;
        }

        IQueryable<MentionCandidate> candidates;
        var usernames = names.ToArray();
        if (community is null)
        {
            candidates = from member in db.ConversationMembers.AsNoTracking()
                         join person in db.ActiveScopePeople.AsNoTracking() on member.UserId equals person.Id
                         where member.ConversationId == conversationId && member.LeftAt == null &&
                               person.Id != sender.Id && (includeOrdinaryActivity || broadcast || usernames.Contains(person.UserName.ToLower()))
                         select new MentionCandidate(person.Id, person.UserName);
        }
        else
        {
            candidates = AnnouncementRecipients.Eligible(db, community)
                .Where(recipient => recipient.Membership.Id != sender.Id &&
                    (includeOrdinaryActivity || broadcast || usernames.Contains(recipient.Membership.UserName.ToLower())))
                .Select(recipient => new MentionCandidate(recipient.Membership.Id, recipient.Membership.UserName));
        }

        var recipients = await candidates.ToListAsync(ct);
        var mentioned = recipients.Where(recipient => broadcast || names.Contains(recipient.Username.ToLowerInvariant()))
            .Select(recipient => recipient.Id).ToHashSet();
        if (mentioned.Count > 0)
        {
            Queue(db, eventKind, resourceId, [.. mentioned], now);
        }

        if (!includeOrdinaryActivity) return;
        var others = recipients.Where(recipient => !mentioned.Contains(recipient.Id)).Select(recipient => recipient.Id).ToArray();
        if (others.Length == 0) return;

        var activityKind = eventKind switch
        {
            MessageKind => MessageActivity,
            PostKind => PostActivity,
            CommentKind => CommentActivity,
            _ => throw new InvalidOperationException("Unsupported activity source.")
        };
        Queue(db, activityKind, resourceId, others, now);
    }

    private static void Queue(NexusDbContext db, string kind, Guid resourceId, Guid[] recipients, DateTimeOffset now)
    {
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            OrganizationId = db.ConversationOrganizationId,
            ResourceId = resourceId,
            RecipientIds = recipients,
            CreatedAt = now,
            AvailableAt = now
        });
    }

    private sealed record MentionCandidate(Guid Id, string Username);
}
