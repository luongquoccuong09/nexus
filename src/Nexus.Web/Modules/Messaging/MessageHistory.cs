using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;

namespace Nexus.Web.Modules.Messaging;

internal static class MessageHistory
{
    public static IQueryable<MessageEntity> VisibleAfter(NexusDbContext db, long visibleFromSeq) =>
        db.Messages.AsNoTracking().Where(message => message.Seq > visibleFromSeq &&
            (message.ParentMessageId == null || db.Messages.Any(parent =>
                parent.Id == message.ParentMessageId && parent.ConversationId == message.ConversationId &&
                parent.Seq > visibleFromSeq)));
}
