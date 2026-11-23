using System.Collections.Concurrent;

namespace Nexus.Web.Modules.Messaging;

/// <summary>
/// In-memory presence and throttled typing tracker (M2.10 — single-instance monolith, no Redis).
/// </summary>
public interface IPresenceTracker
{
    bool OnConnected(Guid organizationId, Guid userId, string connectionId);
    bool OnDisconnected(Guid organizationId, Guid userId, string connectionId);
    bool IsOnline(Guid organizationId, Guid userId);
    bool TryAcquireTypingSlot(Guid userId, Guid conversationId, TimeSpan throttleWindow);
}

public sealed class InMemoryPresenceTracker : IPresenceTracker
{
    private readonly ConcurrentDictionary<(Guid OrganizationId, Guid UserId), ConcurrentDictionary<string, byte>> _online = new();
    private readonly ConcurrentDictionary<(Guid UserId, Guid ConversationId), DateTimeOffset> _typingLastSent = new();

    public bool OnConnected(Guid organizationId, Guid userId, string connectionId)
    {
        var set = _online.GetOrAdd((organizationId, userId), _ => new ConcurrentDictionary<string, byte>());
        var wasEmpty = set.IsEmpty;
        set[connectionId] = 0;
        return wasEmpty;
    }

    public bool OnDisconnected(Guid organizationId, Guid userId, string connectionId)
    {
        if (!_online.TryGetValue((organizationId, userId), out var set))
        {
            return false;
        }

        set.TryRemove(connectionId, out _);
        if (set.IsEmpty)
        {
            _online.TryRemove((organizationId, userId), out _);
            return true;
        }

        return false;
    }

    public bool IsOnline(Guid organizationId, Guid userId) =>
        _online.TryGetValue((organizationId, userId), out var set) && !set.IsEmpty;

    public bool TryAcquireTypingSlot(Guid userId, Guid conversationId, TimeSpan throttleWindow)
    {
        var now = DateTimeOffset.UtcNow;
        var key = (userId, conversationId);

        while (true)
        {
            if (_typingLastSent.TryGetValue(key, out var last))
            {
                if (now - last < throttleWindow)
                {
                    return false;
                }

                if (_typingLastSent.TryUpdate(key, now, last))
                {
                    return true;
                }
            }
            else if (_typingLastSent.TryAdd(key, now))
            {
                return true;
            }
        }
    }
}
