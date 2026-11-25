using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.Calls;

internal static class DirectCallAccess
{
    internal static IQueryable<DirectCallEntity> Eligible(NexusDbContext db) =>
        db.DirectCalls.AsNoTracking().Where(call =>
            db.Users.Any(account => account.Id == call.CallerId && account.Status == AccountStatus.Active) &&
            db.Users.Any(account => account.Id == call.CalleeId && account.Status == AccountStatus.Active) &&
            (call.OrganizationId == null ||
                db.OrganizationMemberships.Any(member => member.OrganizationId == call.OrganizationId &&
                    member.Id == call.CallerId && member.Status == AccountStatus.Active) &&
                db.OrganizationMemberships.Any(member => member.OrganizationId == call.OrganizationId &&
                    member.Id == call.CalleeId && member.Status == AccountStatus.Active)) &&
            db.Conversations.Any(conversation => conversation.Id == call.ConversationId &&
                conversation.OrganizationId == call.OrganizationId && conversation.Kind == ConversationKind.Dm) &&
            db.ConversationMembers.Any(member => member.ConversationId == call.ConversationId &&
                member.UserId == call.CallerId && member.LeftAt == null) &&
            db.ConversationMembers.Any(member => member.ConversationId == call.ConversationId &&
                member.UserId == call.CalleeId && member.LeftAt == null) &&
            !db.UserBlocks.Any(block => block.OrganizationId == (call.OrganizationId ?? Guid.Empty) &&
                (block.BlockerUserId == call.CallerId && block.BlockedUserId == call.CalleeId ||
                 block.BlockerUserId == call.CalleeId && block.BlockedUserId == call.CallerId)));

    internal static IQueryable<DirectCallEntity> Running(IQueryable<DirectCallEntity> calls) =>
        calls.Where(call => call.State == DirectCallState.Ringing || call.State == DirectCallState.Accepted);

    internal static bool MatchesSession(DirectCallEntity call, Guid userId, string? sessionId) =>
        sessionId is not null && (call.CallerId == userId && call.CallerSessionId == sessionId ||
            call.CalleeId == userId && call.CalleeSessionId == sessionId);
}
