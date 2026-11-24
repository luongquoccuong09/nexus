using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Nexus.Web.Infrastructure.Security;

/// <summary>
/// Tracks live SignalR hub connections per user so password changes (M1.5 / SEC-24)
/// and account disablement (M1.6 / SEC-08 / SEC-16) can abort open sockets immediately.
/// </summary>
public interface IUserConnectionRegistry
{
    int ActiveConnectionCount { get; }
    void Register(Guid userId, HubCallerContext callerContext);
    void Unregister(Guid userId, string connectionId);
    IReadOnlyList<string> GetUserConnectionIds(Guid userId);
    int AbortUserConnections(Guid userId);
    void AbortOrganizationConnections(Guid organizationId, Guid userId);
}

public sealed class InMemoryUserConnectionRegistry : IUserConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _connections = new();

    public int ActiveConnectionCount => _connections.Values.Sum(connections => connections.Count);

    public void Register(Guid userId, HubCallerContext callerContext)
    {
        var userSockets = _connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>());
        userSockets[callerContext.ConnectionId] = callerContext;
    }

    public void Unregister(Guid userId, string connectionId)
    {
        if (_connections.TryGetValue(userId, out var userSockets))
        {
            userSockets.TryRemove(connectionId, out _);
            if (userSockets.IsEmpty)
            {
                _connections.TryRemove(userId, out _);
            }
        }
    }

    public IReadOnlyList<string> GetUserConnectionIds(Guid userId) =>
        _connections.TryGetValue(userId, out var sockets) ? sockets.Keys.ToArray() : [];

    public int AbortUserConnections(Guid userId)
    {
        if (!_connections.TryRemove(userId, out var userSockets))
        {
            return 0;
        }

        var aborted = 0;
        foreach (var kvp in userSockets)
        {
            try
            {
                kvp.Value.Abort();
                aborted++;
            }
            catch
            {
                // Best-effort abort for already-closed sockets.
            }
        }

        return aborted;
    }

    public void AbortOrganizationConnections(Guid organizationId, Guid userId)
    {
        if (!_connections.TryGetValue(userId, out var sockets)) return;

        foreach (var socket in sockets.Values)
        {
            if (Modules.Org.OrganizationScope.GetId(socket.GetHttpContext()) == organizationId) socket.Abort();
        }
    }
}
