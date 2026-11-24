using System.Collections.Concurrent;

namespace Nexus.Web.Modules.Messaging;

/// <summary>
/// In-memory presence and throttled typing tracker (M2.10 / M6.28 — single-instance monolith, no Redis).
/// </summary>
public interface IPresenceTracker
{
    int OnlineAccountCount { get; }
    bool OnConnected(Guid organizationId, Guid userId, string connectionId);
    bool OnDisconnected(Guid organizationId, Guid userId, string connectionId);
    bool IsOnline(Guid userId);
    bool IsOnline(Guid organizationId, Guid userId);
    bool TryAcquireTypingSlot(Guid userId, Guid conversationId, TimeSpan throttleWindow);
}

public sealed class InMemoryPresenceTracker : IPresenceTracker
{
    private sealed class UserConnectionState
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, Guid> Connections = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, int> ScopeCounts = new();
    }

    private readonly ConcurrentDictionary<Guid, UserConnectionState> _users = new();
    private readonly ConcurrentDictionary<(Guid UserId, Guid ConversationId), DateTimeOffset> _typingLastSent = new();

    public int OnlineAccountCount => _users.Count;

    public bool OnConnected(Guid organizationId, Guid userId, string connectionId)
    {
        while (true)
        {
            var state = _users.GetOrAdd(userId, _ => new UserConnectionState());
            lock (state.Sync)
            {
                if (!_users.TryGetValue(userId, out var current) || !ReferenceEquals(current, state))
                {
                    continue;
                }

                var wasOffline = state.Connections.Count == 0;
                if (state.Connections.TryAdd(connectionId, organizationId))
                {
                    state.ScopeCounts[organizationId] = state.ScopeCounts.GetValueOrDefault(organizationId) + 1;
                }

                return wasOffline && state.Connections.Count > 0;
            }
        }
    }

    public bool OnDisconnected(Guid organizationId, Guid userId, string connectionId)
    {
        if (!_users.TryGetValue(userId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            if (!state.Connections.Remove(connectionId, out var recordedScope))
            {
                return false;
            }

            if (state.ScopeCounts.TryGetValue(recordedScope, out var count))
            {
                if (count <= 1)
                {
                    state.ScopeCounts.Remove(recordedScope);
                }
                else
                {
                    state.ScopeCounts[recordedScope] = count - 1;
                }
            }

            if (state.Connections.Count == 0)
            {
                _users.TryRemove(new KeyValuePair<Guid, UserConnectionState>(userId, state));
                return true;
            }

            return false;
        }
    }

    public bool IsOnline(Guid userId)
    {
        if (!_users.TryGetValue(userId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            return state.Connections.Count > 0;
        }
    }

    public bool IsOnline(Guid organizationId, Guid userId)
    {
        if (!_users.TryGetValue(userId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            return state.ScopeCounts.GetValueOrDefault(organizationId) > 0 ||
                   state.ScopeCounts.GetValueOrDefault(Guid.Empty) > 0;
        }
    }

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
