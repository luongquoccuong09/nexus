namespace Nexus.Web.Modules.Meetings;

internal sealed class MediaSignalConnections
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Connection> _connections = [];

    internal void Register(Guid grantId, string roomName, CancellationTokenSource lifetime)
    {
        Connection? previous;
        lock (_gate)
        {
            _connections.TryGetValue(grantId, out previous);
            _connections[grantId] = new(roomName, lifetime);
        }

        previous?.Cancel();
    }

    internal void Unregister(Guid grantId, CancellationTokenSource lifetime)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(grantId, out var connection) && connection.Lifetime == lifetime)
                _connections.Remove(grantId);
        }
    }

    internal void Disconnect(Guid grantId, TimeSpan grace)
    {
        Connection? connection;
        lock (_gate) _connections.TryGetValue(grantId, out connection);
        connection?.Cancel(grace);
    }

    internal void DisconnectRoom(string roomName, TimeSpan grace)
    {
        Connection[] connections;
        lock (_gate) connections = _connections.Values.Where(connection => connection.RoomName == roomName).ToArray();
        foreach (var connection in connections) connection.Cancel(grace);
    }

    private sealed record Connection(string RoomName, CancellationTokenSource Lifetime)
    {
        internal void Cancel(TimeSpan grace = default)
        {
            try
            {
                if (grace == TimeSpan.Zero) Lifetime.Cancel();
                else Lifetime.CancelAfter(grace);
            }
            catch (ObjectDisposedException) { }
        }
    }
}
