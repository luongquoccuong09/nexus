using System.Diagnostics;
using System.Diagnostics.Metrics;
using Nexus.Monitoring;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.Monitor;

internal sealed record PerformancePoint(
    DateTimeOffset Timestamp, double ProcessCpuCores, long ProcessMemoryBytes, double UptimeSeconds,
    int Threads, long QueuedWork, double GcPauseMs, int OnlineAccounts, int Sockets, double RequestsPerSecond,
    long Http4xx, long Http429, long Http5xx, double? MeanMs, double? P50Ms, double? P95Ms, double? P99Ms,
    long Messages, long Posts, long Comments, long Reactions, long Uploads, double? FanoutP95Ms,
    double? DbP95Ms, long? PoolUsed, long? PoolIdle);

internal sealed class PerformanceSampler(IUserConnectionRegistry connections, IPresenceTracker presence) : BackgroundService
{
    private static readonly double[] UpperBounds = [1, 2, 5, 10, 20, 35, 50, 75, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2500, 5000, 10000, 30000];
    private readonly object _sync = new();
    private readonly Queue<PerformancePoint> _samples = new();
    private readonly long[] _traffic = new long[5];
    private readonly Distribution _http = new();
    private readonly Distribution _fanout = new();
    private readonly Distribution _database = new();
    private long? _poolUsed, _poolIdle;
    private long _http4xx, _http429, _http5xx;

    public PerformancePoint[] Read(int seconds)
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-seconds);
        lock (_sync) return _samples.Where(x => x.Timestamp > since).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == NexusMeters.Name || instrument.Meter.Name == "Npgsql" ||
                instrument.Meter.Name == "Microsoft.AspNetCore.Hosting" && instrument.Name == "http.server.request.duration")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => ObserveCount(instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => ObserveCount(instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => ObserveDuration(instrument.Name, value, tags));
        listener.Start();

        using var process = Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var lastCpu = process.TotalProcessorTime;
        var lastPause = GC.GetTotalPauseDuration();
        var lastSample = Stopwatch.GetTimestamp();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(lastSample, timestamp).TotalSeconds;
            var cpu = process.TotalProcessorTime;
            var pause = GC.GetTotalPauseDuration();
            var onlineAccounts = presence.OnlineAccountCount;
            var sockets = connections.ActiveConnectionCount;
            listener.RecordObservableInstruments();
            lock (_sync)
            {
                _samples.Enqueue(new PerformancePoint(now, (cpu - lastCpu).TotalSeconds / elapsed, process.WorkingSet64,
                    (now - process.StartTime.ToUniversalTime()).TotalSeconds, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                    (pause - lastPause).TotalMilliseconds, onlineAccounts, sockets, _http.Count / elapsed,
                    _http4xx, _http429, _http5xx, _http.Mean, _http.Percentile(.5), _http.Percentile(.95), _http.Percentile(.99),
                    _traffic[0], _traffic[1], _traffic[2], _traffic[3], _traffic[4], _fanout.Percentile(.95),
                    _database.Percentile(.95), _poolUsed, _poolIdle));
                while (_samples.Count > 720) _samples.Dequeue();
                Array.Clear(_traffic);
                _http.Clear();
                _fanout.Clear();
                _database.Clear();
                _poolUsed = _poolIdle = null;
                _http4xx = _http429 = _http5xx = 0;
            }
            lastSample = timestamp;
            lastCpu = cpu;
            lastPause = pause;
        }
    }

    private void ObserveCount(string name, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (name == "db.client.connection.count")
        {
            foreach (var tag in tags)
            {
                if (tag.Key is not ("state" or "db.client.connection.state")) continue;
                lock (_sync)
                {
                    if (tag.Value is "used") _poolUsed = value;
                    if (tag.Value is "idle") _poolIdle = value;
                }
            }
            return;
        }
        var index = name switch
        {
            "nexus.messages.created" => 0, "nexus.posts.created" => 1, "nexus.comments.created" => 2,
            "nexus.reactions.created" => 3, "nexus.uploads.created" => 4, _ => -1
        };
        if (index >= 0) lock (_sync) _traffic[index] += value;
    }

    private void ObserveDuration(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (name == "db.client.operation.duration")
        {
            lock (_sync) _database.Add(value * 1000);
            return;
        }

        if (name == "nexus.messaging.fanout")
        {
            lock (_sync) _fanout.Add(value);
            return;
        }
        var status = 0;
        foreach (var tag in tags)
        {
            if (tag.Key == "http.route" && tag.Value is string route &&
                (route.StartsWith("/api/monitor", StringComparison.Ordinal) || route is "/healthz" or "/readyz"))
                return;
            if (tag.Key == "http.response.status_code" && tag.Value is int code) status = code;
        }
        lock (_sync)
        {
            _http.Add(value * 1000);
            if (status is >= 400 and < 500) _http4xx++;
            if (status == 429) _http429++;
            if (status >= 500) _http5xx++;
        }
    }

    private sealed class Distribution
    {
        private readonly long[] _counts = new long[UpperBounds.Length + 1];
        private double _sum;
        public long Count { get; private set; }
        public double? Mean => Count == 0 ? null : _sum / Count;

        public void Add(double milliseconds)
        {
            var index = Array.BinarySearch(UpperBounds, milliseconds);
            _counts[index < 0 ? ~index : index]++;
            _sum += milliseconds;
            Count++;
        }

        public double? Percentile(double quantile)
        {
            if (Count == 0) return null;
            var rank = Math.Ceiling(Count * quantile);
            for (var i = 0; i < UpperBounds.Length; i++)
            {
                rank -= _counts[i];
                if (rank <= 0) return UpperBounds[i];
            }
            return null;
        }

        public void Clear()
        {
            Array.Clear(_counts);
            _sum = 0;
            Count = 0;
        }
    }
}

internal sealed record SandboxPoint(DateTimeOffset Timestamp, double Rps, int Users, int Sockets,
    double? CpuPercent, long? MemoryBytes, long? LimitBytes, long Failed, long Throttled);

internal sealed class SandboxSamples
{
    private readonly Queue<SandboxPoint> _samples = new();
    private Guid _id;

    public void Add(Guid id, LoadSnapshot snapshot, DateTimeOffset now)
    {
        lock (_samples)
        {
            if (_id != id)
            {
                _samples.Clear();
                _id = id;
            }
            _samples.Enqueue(new(now, snapshot.RequestsPerSecond, snapshot.ActiveUsers, snapshot.Sockets,
                snapshot.CpuPercent, snapshot.MemoryBytes, snapshot.MemoryLimitBytes, snapshot.Failures, snapshot.Throttled));
            while (_samples.Count > 720) _samples.Dequeue();
        }
    }

    public SandboxPoint[] Read(Guid id, int seconds)
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-seconds);
        lock (_samples) return _id == id ? _samples.Where(x => x.Timestamp > since).ToArray() : [];
    }
}
