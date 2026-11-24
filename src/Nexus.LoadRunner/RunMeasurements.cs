using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Nexus.Monitoring;

namespace Nexus.LoadRunner;

internal sealed class RunMeasurements
{
    private readonly Dictionary<string, LatencyHistogram> _latencies =
        LoadSnapshot.Operations.ToDictionary(operation => operation, _ => new LatencyHistogram(), StringComparer.Ordinal);
    private readonly Stopwatch _time = new();
    private readonly object _sync = new();
    private long _successes, _failures, _throttled, _steadySuccesses, _steadyFailures, _steadyThrottled, _steadyWorkload;
    private long _messages, _posts, _comments, _uploads, _reconnects, _syncFailures, _duplicates;
    private long _lastRequests, _lastSample = Stopwatch.GetTimestamp();
    private double _rps;
    private int _users, _targetSeconds;
    private double? _peakCpu;
    private long? _peakMemory;
    private ContainerSample? _container;
    private SandboxTarget? _target;

    public RunMeasurements(LoadConfiguration config)
    {
        Configuration = config;
        Deliveries = new DeliveryTracker(Latency);
    }

    public LoadConfiguration Configuration { get; }
    public DeliveryTracker Deliveries { get; }
    public ConcurrentDictionary<int, VirtualUser> Connected { get; } = new();
    public TimeSpan Elapsed => _time.Elapsed;
    public bool Steady => _time.IsRunning && _time.Elapsed.TotalMinutes >= Configuration.RampMinutes;
    public int Users => Volatile.Read(ref _users);
    public void Start(SandboxTarget target)
    {
        _target = target;
        _lastSample = Stopwatch.GetTimestamp();
        _time.Start();
    }

    public void EndMeasurement() => _time.Stop();
    public void Joined() => Interlocked.Increment(ref _users);
    public void Left() => Interlocked.Decrement(ref _users);
    public void Reconnected() => Interlocked.Increment(ref _reconnects);
    public void SyncFailed() => Interlocked.Increment(ref _syncFailures);
    public void DuplicateMessage() => Interlocked.Increment(ref _duplicates);

    public void Request(string operation, HttpStatusCode? status, double ms, bool steady)
    {
        if (status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
        {
            Interlocked.Increment(ref _successes);
            if (steady)
            {
                Interlocked.Increment(ref _steadySuccesses);
                if (operation is "read" or "direct" or "group" or "post" or "comment")
                    Interlocked.Increment(ref _steadyWorkload);
                Latency(operation, ms);
            }
            if (status == HttpStatusCode.Created)
            {
                if (operation is "direct" or "group") Interlocked.Increment(ref _messages);
                if (operation == "post") Interlocked.Increment(ref _posts);
                if (operation == "comment") Interlocked.Increment(ref _comments);
                if (operation == "upload") Interlocked.Increment(ref _uploads);
            }
        }
        else
        {
            Interlocked.Increment(ref _failures);
            if (steady) Interlocked.Increment(ref _steadyFailures);
            if (status == HttpStatusCode.TooManyRequests)
            {
                Interlocked.Increment(ref _throttled);
                if (steady) Interlocked.Increment(ref _steadyThrottled);
            }
        }
    }

    public void Latency(string operation, double ms) => _latencies[operation].Record(ms);

    public void Sample(ContainerSample? sample, int interval)
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            var requests = Interlocked.Read(ref _successes) + Interlocked.Read(ref _failures);
            _rps = (requests - _lastRequests) / Math.Max(1, Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds);
            _lastRequests = requests;
            _lastSample = now;
            _container = sample;
            if (sample is not null)
            {
                _peakCpu = Math.Max(_peakCpu.GetValueOrDefault(), sample.CpuPercent);
                if (sample.MemoryBytes is { } bytes) _peakMemory = Math.Max(_peakMemory.GetValueOrDefault(), bytes);
            }
            if (Steady && Users == Configuration.Users && (!Configuration.SignalR || Connected.Count == Configuration.Users))
                _targetSeconds = Math.Min((int)Math.Max(0, Elapsed.TotalSeconds - Configuration.RampMinutes * 60), _targetSeconds + interval);
        }
    }

    public LoadSnapshot Snapshot(string phase, string? reason = null)
    {
        lock (_sync)
        {
            var latencies = _latencies.Select(pair => pair.Value.Read(pair.Key)).OfType<LatencySummary>().ToArray();
            return new LoadSnapshot(phase, (int)Elapsed.TotalSeconds, Users, Connected.Count, _rps,
                Interlocked.Read(ref _successes), Interlocked.Read(ref _failures), Interlocked.Read(ref _throttled),
                Interlocked.Read(ref _messages), Interlocked.Read(ref _posts), Interlocked.Read(ref _comments),
                Interlocked.Read(ref _reconnects), Interlocked.Read(ref _syncFailures), Deliveries.Delivered, Deliveries.Recovered,
                Deliveries.Missing, Deliveries.Duplicates, _container?.CpuPercent, _container?.MemoryBytes,
                _container?.MemoryLimitBytes, _peakCpu, _peakMemory, _target?.Version, _target?.Commit, _target?.ImageId, latencies)
            {
                SteadySuccesses = Interlocked.Read(ref _steadySuccesses), SteadyFailures = Interlocked.Read(ref _steadyFailures),
                SteadyThrottled = Interlocked.Read(ref _steadyThrottled), SteadyWorkload = Interlocked.Read(ref _steadyWorkload),
                TargetReachedSeconds = _targetSeconds, DuplicateMessages = Interlocked.Read(ref _duplicates), Reason = reason,
                Uploads = Interlocked.Read(ref _uploads),
                NetworkInBytes = _container?.NetworkInBytes, NetworkOutBytes = _container?.NetworkOutBytes,
                DiskReadBytes = _container?.DiskReadBytes, DiskWriteBytes = _container?.DiskWriteBytes
            };
        }
    }

    private sealed class LatencyHistogram
    {
        private static readonly double[] Bounds = Enumerable.Range(0, 151).Select(i => Math.Pow(1.08, i)).ToArray();
        private readonly long[] _buckets = new long[Bounds.Length + 1];
        private long _count;
        private double _sum;

        public void Record(double ms)
        {
            var bin = Array.BinarySearch(Bounds, ms);
            lock (_buckets)
            {
                _buckets[bin < 0 ? ~bin : bin]++;
                _count++;
                _sum += ms;
            }
        }

        public LatencySummary? Read(string operation)
        {
            lock (_buckets)
            {
                return _count == 0 ? null :
                    new LatencySummary(operation, _count, _sum / _count, Percentile(.50), Percentile(.95), Percentile(.99));
            }
        }

        private double Percentile(double quantile)
        {
            var rank = Math.Ceiling(_count * quantile);
            for (var i = 0; i < Bounds.Length; i++)
            {
                rank -= _buckets[i];
                if (rank <= 0) return Bounds[i];
            }
            return 10_000_000;
        }
    }
}
