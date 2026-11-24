using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nexus.LoadRunner;

internal sealed class DeliveryTracker(Action<string, double> latency)
{
    private readonly ConcurrentDictionary<Guid, Delivery> _pending = new();
    private long _delivered, _duplicates, _recovered, _missing;
    public long Delivered => Interlocked.Read(ref _delivered);
    public long Duplicates => Interlocked.Read(ref _duplicates);
    public long Recovered => Interlocked.Read(ref _recovered);
    public long Missing => Interlocked.Read(ref _missing);

    public void Begin(Guid key, Guid conversationId, IEnumerable<int> users, bool steady)
    {
        var expected = users.ToHashSet();
        if (expected.Count != 0)
            _pending.TryAdd(key, new Delivery(conversationId, Stopwatch.GetTimestamp(), steady, expected));
    }

    public void End(Guid key, long? sequence)
    {
        if (!_pending.TryGetValue(key, out var tracked)) return;
        if (sequence is null)
        {
            _pending.TryRemove(key, out _);
            return;
        }
        lock (tracked) tracked.Sequence = sequence;
    }

    public void Receive(Guid key, int user)
    {
        if (!_pending.TryGetValue(key, out var tracked)) return;
        lock (tracked)
        {
            if (tracked.Expected.Remove(user))
            {
                tracked.Received.Add(user);
                Interlocked.Increment(ref _delivered);
                if (tracked.Steady)
                    latency("delivery", Stopwatch.GetElapsedTime(tracked.Sent).TotalMilliseconds);
            }
            else if (tracked.Received.Contains(user))
            {
                Interlocked.Increment(ref _duplicates);
            }
        }
    }

    public async Task ReconcileAsync(ConcurrentDictionary<int, VirtualUser> connected, int budget, CancellationToken ct)
    {
        foreach (var (key, tracked) in _pending)
        {
            if (Stopwatch.GetElapsedTime(tracked.Sent) < TimeSpan.FromSeconds(5)) continue;
            int[] expected;
            long? sequence;
            lock (tracked)
            {
                expected = tracked.Expected.ToArray();
                sequence = tracked.Sequence;
            }

            if (sequence is null)
            {
                if (Stopwatch.GetElapsedTime(tracked.Sent) > TimeSpan.FromSeconds(25)) _pending.TryRemove(key, out _);
                continue;
            }

            foreach (var index in expected)
            {
                if (budget == 0) break;
                if (!connected.TryGetValue(index, out var user)) continue;
                budget--;
                if (await user.HasMessageAsync(tracked.ConversationId, sequence.Value, key, ct))
                {
                    lock (tracked)
                    {
                        if (tracked.Expected.Remove(index)) Interlocked.Increment(ref _recovered);
                    }
                }
            }

            lock (tracked)
            {
                if (tracked.Expected.Count == 0 || Stopwatch.GetElapsedTime(tracked.Sent) >= TimeSpan.FromSeconds(25))
                {
                    Interlocked.Add(ref _missing, tracked.Expected.Count);
                    _pending.TryRemove(key, out _);
                }
            }
        }
    }

    public void Finish()
    {
        foreach (var tracked in _pending.Values)
        {
            lock (tracked)
            {
                if (tracked.Sequence is not null) Interlocked.Add(ref _missing, tracked.Expected.Count);
            }
        }
        _pending.Clear();
    }

    private sealed record Delivery(Guid ConversationId, long Sent, bool Steady, HashSet<int> Expected)
    {
        public HashSet<int> Received { get; } = [];
        public long? Sequence { get; set; }
    }
}
