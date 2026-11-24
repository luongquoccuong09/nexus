using System.Text.Json.Serialization;

namespace Nexus.Monitoring;

public sealed record LoadConfiguration(
    string Profile, int Users, int DurationMinutes, int RampMinutes,
    int MessagesPerMinute, int PostsPerHour, int CommentsPerHour, int ReadsPerMinute,
    bool DirectMessages, bool GroupChat, bool CommunityPosts, bool Comments,
    bool Reactions, bool Uploads, bool Typing, bool SignalR, bool Reconnect, bool OfflineSync)
{
    [JsonIgnore]
    public bool IsValid =>
        Profile is "sandbox-capacity" or "sandbox-policy" &&
        Users is >= 1 and <= 300 && DurationMinutes is >= 1 and <= 60 &&
        RampMinutes is >= 0 and <= 10 && RampMinutes < DurationMinutes &&
        MessagesPerMinute is >= 0 and <= 20 && PostsPerHour is >= 0 and <= 20 &&
        CommentsPerHour is >= 0 and <= 60 && ReadsPerMinute is >= 0 and <= 30 &&
        (!Reconnect && !OfflineSync && !Typing || SignalR) && (!Comments && !Reactions || CommunityPosts) &&
        (!Uploads || DirectMessages || GroupChat) && (!OfflineSync || DirectMessages || GroupChat);
}

public sealed record LoadLease(Guid Id, Guid Generation, LoadConfiguration Configuration);
public sealed record HeartbeatReply(bool StopRequested);
public sealed record RunnerUpdate(Guid Generation, LoadSnapshot Snapshot);
public sealed record RunnerResult(Guid Generation, string Outcome, bool Cleaned, LoadSnapshot Snapshot);
public sealed record LatencySummary(string Operation, long Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms);
public sealed record LoadSnapshot(
    string Phase, int ElapsedSeconds, int ActiveUsers, int Sockets,
    double RequestsPerSecond, long Successes, long Failures, long Throttled,
    long Messages, long Posts, long Comments, long Reconnects, long SyncFailures,
    long Delivered, long Recovered, long Missing, long DuplicateEvents,
    double? CpuPercent, long? MemoryBytes, long? MemoryLimitBytes,
    double? PeakCpuPercent, long? PeakMemoryBytes,
    string? Version, string? Commit, string? ImageId, LatencySummary[] Latencies)
{
    public long SteadySuccesses { get; init; }
    public long SteadyFailures { get; init; }
    public long SteadyThrottled { get; init; }
    public long SteadyWorkload { get; init; }
    public int TargetReachedSeconds { get; init; }
    public long DuplicateMessages { get; init; }
    public long Uploads { get; init; }
    public long? NetworkInBytes { get; init; }
    public long? NetworkOutBytes { get; init; }
    public long? DiskReadBytes { get; init; }
    public long? DiskWriteBytes { get; init; }
    public string? Reason { get; init; }
    public string? BenchmarkOutcome { get; init; }
    public string? BenchmarkCriteria { get; init; }

    public static readonly string[] Reasons =
    [
        "image_unavailable", "invalid_seed_manifest", "sandbox_container_missing", "unexpected_sandbox_binding",
        "unexpected_sandbox_build", "sandbox_not_ready", "docker_unavailable", "docker_image_failed",
        "docker_compose_failed", "docker_stats_failed", "provisioning_deadline", "control_lease_expired",
        "memory_limit", "target_unhealthy", "error_budget", "emergency_stop", "operator_stop",
        "runner_interrupted", "runner_failed", "invalid_configuration"
    ];

    public static readonly string[] Operations =
    [
        "login", "direct", "group", "read", "post", "comment", "reaction", "upload",
        "sync", "signalr", "delivery"
    ];

    [JsonIgnore]
    public bool IsValid =>
        Phase is "provisioning" or "ramp" or "steady" or "cleanup" or "completed" or
            "stopped" or "failed" or "safety_stop" &&
        ElapsedSeconds is >= 0 and <= 5_000 && ActiveUsers is >= 0 and <= 300 && Sockets is >= 0 and <= 300 &&
        ValidRate(RequestsPerSecond) && ValidRate(CpuPercent ?? 0) && ValidRate(PeakCpuPercent ?? 0) &&
        MemoryBytes is null or > 0 && MemoryLimitBytes is null or > 0 && PeakMemoryBytes is null or > 0 &&
        (Reason is null || Reasons.Contains(Reason)) && BenchmarkOutcome is null && BenchmarkCriteria is null && SteadySuccesses >= 0 && SteadyFailures >= 0 &&
        SteadyThrottled >= 0 && SteadyWorkload >= 0 && TargetReachedSeconds is >= 0 and <= 3600 && DuplicateMessages >= 0 && Uploads >= 0 &&
        NetworkInBytes is null or >= 0 && NetworkOutBytes is null or >= 0 && DiskReadBytes is null or >= 0 && DiskWriteBytes is null or >= 0 &&
        Successes >= 0 && Failures >= 0 && Throttled >= 0 && Messages >= 0 && Posts >= 0 && Comments >= 0 &&
        Reconnects >= 0 && SyncFailures >= 0 && Delivered >= 0 && Recovered >= 0 && Missing >= 0 && DuplicateEvents >= 0 &&
        (Version is null || Version.Length <= 32 && System.Version.TryParse(Version, out _)) &&
        (Commit is null || Commit.Length == 40 && Commit.All(Uri.IsHexDigit)) &&
        (ImageId is null || ImageId.Length == 71 && ImageId.StartsWith("sha256:", StringComparison.Ordinal) && ImageId[7..].All(Uri.IsHexDigit)) &&
        Latencies is { Length: <= 11 } && Latencies.Select(x => x?.Operation).Distinct().Count() == Latencies.Length &&
        Latencies.All(x => x is not null && Operations.Contains(x.Operation) && x.Count > 0 &&
            ValidRate(x.MeanMs) && ValidRate(x.P50Ms) && ValidRate(x.P95Ms) && ValidRate(x.P99Ms) &&
            x.P50Ms <= x.P95Ms && x.P95Ms <= x.P99Ms);

    private static bool ValidRate(double value) => double.IsFinite(value) && value >= 0 && value <= 10_000_000;
}

public sealed record SandboxFixture(Guid OrganizationId, Guid ChannelId, Guid GroupId, Guid[] DirectIds, Guid[] UserIds, Guid SeedPostId);
