using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Monitoring;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Org;
using Npgsql;
using ResourceAuthorization = Nexus.Web.Infrastructure.Security.IAuthorizationService;

namespace Nexus.Web.Modules.Monitor;

internal static class MonitorEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReportsJson = new(Json) { WriteIndented = true };
    private const string RunnerHeader = "X-Nexus-Runner-Key";
    private const string BaselineCriteria = "collaboration-60-v1";

    public static void AddMonitor(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<MonitorOptions>().BindConfiguration(MonitorOptions.SectionName)
            .Validate(options => options.LeaseSeconds is >= 15 and <= 120 && !string.IsNullOrWhiteSpace(options.EnvironmentName) &&
                (options.RunnerKey is null || options.RunnerKey.Length is >= 32 and <= 128 &&
                    options.RunnerKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')), "Invalid Monitor configuration")
            .ValidateOnStart();
        builder.Services.AddSingleton<PerformanceSampler>();
        builder.Services.AddSingleton<SandboxSamples>();
        if (builder.Configuration.GetValue<bool>("NexusMonitor:Enabled"))
            builder.Services.AddHostedService(services => services.GetRequiredService<PerformanceSampler>());
    }

    public static void MapMonitor(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<MonitorOptions>>().Value.Enabled) return;

        var monitor = app.MapGroup("/api/monitor").RequireAuthorization().AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            return Can(context, ResourceAction.MonitorRead) ? await next(invocation) : Results.Forbid();
        });
        monitor.MapGet("/capabilities", (HttpContext ctx, IOptionsMonitor<MonitorOptions> options) => new
        {
            canRun = ctx.User.TryGetUserId(out var id) && options.CurrentValue.Operators.Contains(id),
            mfaVerified = HasMfa(ctx),
            runnerConfigured = options.CurrentValue.RunnerKey is not null,
            environment = options.CurrentValue.EnvironmentName
        });
        monitor.MapGet("/performance", Performance);
        monitor.MapGet("/runs/{id:guid}/performance", (Guid id, SandboxSamples samples) =>
            Results.Ok(new { environment = "Docker Sandbox", samples = samples.Read(id, 3600) }));
        monitor.MapGet("/runs", History);
        monitor.MapGet("/runs/{id:guid}", Detail);
        monitor.MapGet("/runs/{id:guid}/report", Report);
        monitor.MapPost("/runs", Start).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        monitor.MapPost("/runs/{id:guid}/stop", Stop).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);

        var runner = app.MapGroup("/api/monitor/runner").AllowAnonymous().AddEndpointFilter(async (invocation, next) =>
        {
            var ctx = invocation.HttpContext;
            ctx.Response.Headers.CacheControl = "no-store";
            var key = ctx.RequestServices.GetRequiredService<IOptionsMonitor<MonitorOptions>>().CurrentValue.RunnerKey;
            if ((!ctx.Request.IsHttps && !(app.Environment.IsDevelopment() &&
                    ctx.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address))) ||
                key is null || !ctx.Request.Headers.TryGetValue(RunnerHeader, out var supplied) || supplied.Count != 1)
                return Results.Unauthorized();
            var entered = supplied.ToString();
            return entered.Length <= 128 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(entered)), SHA256.HashData(Encoding.UTF8.GetBytes(key)))
                ? await next(invocation) : Results.Unauthorized();
        });
        runner.MapPost("/claim", Claim);
        runner.MapPost("/runs/{id:guid}/heartbeat", Heartbeat);
        runner.MapPost("/runs/{id:guid}/result", Result);
    }

    public static bool CanView(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IOptionsMonitor<MonitorOptions>>().CurrentValue.Enabled &&
        Can(ctx, ResourceAction.MonitorRead);

    private static bool HasMfa(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimsPrincipalExtensions.MfaVerifiedClaimType) == "true";

    private static bool Can(HttpContext ctx, ResourceAction action)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return false;
        // The authenticated HTTP pipeline has already loaded and checked the active account and security stamp.
        var options = ctx.RequestServices.GetRequiredService<IOptionsMonitor<MonitorOptions>>().CurrentValue;
        if (!options.Enabled) return false;
        var canExecute = options.Operators.Contains(userId);
        var actor = new ActorContext(userId, Guid.Empty, AccountStatus.Active, TrustLevel.Unverified)
        {
            CanMonitor = canExecute || options.Readers.Contains(userId),
            CanRunLoadTests = canExecute && HasMfa(ctx)
        };
        return ctx.RequestServices.GetRequiredService<ResourceAuthorization>()
            .Can(actor, action, new ResourceContext(ResourceKind.Monitor));
    }

    private static IResult Performance(string? range, PerformanceSampler sampler, IOptionsMonitor<MonitorOptions> options)
    {
        var seconds = range switch { null or "5m" => 300, "15m" => 900, "1h" => 3600, _ => 0 };
        return seconds == 0 ? Results.BadRequest(new { code = "invalid_range" }) :
            Results.Ok(new { environment = options.CurrentValue.EnvironmentName, samples = sampler.Read(seconds) });
    }

    private static async Task<IResult> Start(StartRequest request, HttpContext ctx, NexusDbContext db,
        IOptionsMonitor<MonitorOptions> options, CancellationToken ct)
    {
        if (!Can(ctx, ResourceAction.MonitorExecute)) return Results.Forbid();
        if (options.CurrentValue.RunnerKey is null) return Results.Conflict(new { code = "runner_not_configured" });
        if (request.RequestId == Guid.Empty || request.Configuration is not { IsValid: true })
            return Results.BadRequest(new { code = "invalid_load_configuration" });

        ctx.User.TryGetUserId(out var actorId);
        var existing = await db.Set<MonitorRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RequestId, ct);
        if (existing is not null) return Replay(existing, request, actorId);

        var run = new MonitorRun
        {
            Id = request.RequestId, ActorId = actorId, CreatedAt = DateTimeOffset.UtcNow,
            Configuration = JsonSerializer.Serialize(request.Configuration, Json)
        };
        db.Add(run);
        Audit(db, run.Id, "queued", actorId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            existing = await db.Set<MonitorRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.RequestId, ct);
            return existing is null ? Results.Conflict(new { code = "active_run_exists" }) : Replay(existing, request, actorId);
        }
        return Accepted(run.Id);
    }

    private static IResult Accepted(Guid id) => Results.Accepted($"/api/monitor/runs/{id}", new { id });

    private static IResult Replay(MonitorRun run, StartRequest request, Guid actorId) =>
        run.ActorId == actorId && JsonSerializer.Deserialize<LoadConfiguration>(run.Configuration, Json) == request.Configuration
            ? Accepted(run.Id) : Results.Conflict(new { code = "request_id_conflict" });

    private static async Task<IResult> History(Guid? before, NexusDbContext db, CancellationToken ct)
    {
        var source = db.Set<MonitorRun>().AsNoTracking();
        if (before is { } id)
        {
            var cursor = await source.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (cursor is null) return Results.BadRequest(new { code = "invalid_cursor" });
            source = source.Where(x => x.CreatedAt < cursor.CreatedAt ||
                x.CreatedAt == cursor.CreatedAt && x.Id.CompareTo(cursor.Id) < 0);
        }

        var page = await source.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(51).ToListAsync(ct);
        return Results.Ok(new { items = page.Take(50).Select(ToView), nextCursor = page.Count > 50 ? page[49].Id : (Guid?)null });
    }

    private static async Task<IResult> Detail(Guid id, NexusDbContext db, CancellationToken ct)
    {
        var run = await db.Set<MonitorRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null) return Results.NotFound();
        var events = await db.Set<MonitorAudit>().AsNoTracking().Where(x => x.RunId == id)
            .OrderByDescending(x => x.Id).Take(25)
            .Select(x => new { x.OccurredAt, x.Action }).ToListAsync(ct);
        return Results.Ok(new { run = ToView(run), events });
    }

    private static async Task<IResult> Report(Guid id, string? format, NexusDbContext db, CancellationToken ct)
    {
        if (format is not (null or "json" or "html")) return Results.BadRequest(new { code = "invalid_report_format" });
        var run = await db.Set<MonitorRun>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null) return Results.NotFound();
        var json = JsonSerializer.Serialize(ToView(run), ReportsJson);
        if (format != "html") return Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"nexus-load-{id}.json");
        var html = $"""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Nexus Load Test Report</title><h1>Nexus Load Test Report</h1>
            <pre>{WebUtility.HtmlEncode(json)}</pre></html>
            """;
        return Results.File(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", $"nexus-load-{id}.html");
    }

    private static async Task<IResult> Stop(Guid id, HttpContext ctx, NexusDbContext db, CancellationToken ct)
    {
        if (!Can(ctx, ResourceAction.MonitorExecute)) return Results.Forbid();
        ctx.User.TryGetUserId(out var actorId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var run = await db.Set<MonitorRun>().FindAsync([id], ct);
            if (run is null) return Results.NotFound();
            if (!run.Active || run.StopRequested) return Accepted(id);
            run.StopRequested = true;
            if (run.Generation is null) Finish(run, "stopped", true);
            Audit(db, id, "stop_requested", actorId);
            if (await Save(db, ct)) return Accepted(id);
        }
        return Results.Conflict(new { code = "run_state_changed" });
    }

    private static async Task<IResult> Claim(NexusDbContext db, IOptionsMonitor<MonitorOptions> options,
        IAccountStatusLookup accounts, CancellationToken ct)
    {
        var run = await db.Set<MonitorRun>().SingleOrDefaultAsync(x => x.Active && x.Generation == null, ct);
        if (run is null) return Results.NoContent();
        // A queued command has a short lifetime. It cannot unexpectedly start hours later.
        if (DateTimeOffset.UtcNow - run.CreatedAt > TimeSpan.FromMinutes(2))
        {
            Finish(run, "expired", true);
            Audit(db, run.Id, "expired");
            await Save(db, ct);
            return Results.NoContent();
        }
        if (!options.CurrentValue.Operators.Contains(run.ActorId) || await accounts.GetStatusAsync(run.ActorId, ct) != AccountStatus.Active)
        {
            Finish(run, "stopped", true);
            Audit(db, run.Id, "operator_access_revoked");
            await Save(db, ct);
            return Results.NoContent();
        }
        run.Generation = Guid.NewGuid();
        run.Status = "provisioning";
        run.StartedAt = run.HeartbeatAt = DateTimeOffset.UtcNow;
        Audit(db, run.Id, "claimed");
        return await Save(db, ct)
            ? Results.Ok(new { lease = new LoadLease(run.Id, run.Generation.Value,
                JsonSerializer.Deserialize<LoadConfiguration>(run.Configuration, Json)!), leaseSeconds = options.CurrentValue.LeaseSeconds })
            : Results.NoContent();
    }

    private static async Task<IResult> Heartbeat(Guid id, RunnerUpdate update, NexusDbContext db,
        SandboxSamples samples, IOptionsMonitor<MonitorOptions> options, CancellationToken ct)
    {
        if (update.Snapshot is not { IsValid: true }) return Results.BadRequest();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var run = await db.Set<MonitorRun>().FindAsync([id], ct);
            if (run is null || run.Generation != update.Generation) return Results.NotFound();
            if (!run.Active || run.HeartbeatAt is not { } last ||
                DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(options.CurrentValue.LeaseSeconds))
                return Results.Ok(new HeartbeatReply(true));
            if (run.Status != update.Snapshot.Phase) Audit(db, id, update.Snapshot.Phase);
            run.Status = update.Snapshot.Phase;
            run.HeartbeatAt = DateTimeOffset.UtcNow;
            run.Latest = JsonSerializer.Serialize(update.Snapshot, Json);
            if (!await Save(db, ct)) continue;
            samples.Add(id, update.Snapshot, run.HeartbeatAt.Value);
            return Results.Ok(new HeartbeatReply(run.StopRequested));
        }
        return Results.Conflict();
    }

    private static async Task<IResult> Result(Guid id, RunnerResult result, NexusDbContext db, CancellationToken ct)
    {
        if (result.Outcome is not ("completed" or "stopped" or "failed" or "safety_stop") ||
            result.Snapshot is not { IsValid: true } || result.Snapshot.Phase != result.Outcome) return Results.BadRequest();
        var run = await db.Set<MonitorRun>().FindAsync([id], ct);
        if (run is null || run.Generation != result.Generation) return Results.NotFound();
        if (!run.Active) return Results.NoContent();

        var summary = result.Snapshot;
        if (result.Cleaned && result.Outcome == "completed")
        {
            var config = JsonSerializer.Deserialize<LoadConfiguration>(run.Configuration, Json);
            summary = summary with { BenchmarkOutcome = BenchmarkResult(config, summary), BenchmarkCriteria = BaselineCriteria };
        }
        run.Latest = JsonSerializer.Serialize(summary, Json);
        if (result.Cleaned)
            Finish(run, result.Outcome, true);
        else
        {
            run.Status = "cleanup_required";
            run.Cleaned = false;
        }
        Audit(db, id, result.Cleaned ? result.Outcome : "cleanup_required");
        return await Save(db, ct) ? Results.NoContent() : Results.Conflict();
    }

    private static void Finish(MonitorRun run, string state, bool cleaned)
    {
        run.Status = state;
        run.Cleaned = cleaned;
        run.Active = false;
        run.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<bool> Save(NexusDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
    }

    private static void Audit(NexusDbContext db, Guid runId, string action, Guid? actorId = null) =>
        db.Add(new MonitorAudit { RunId = runId, Action = action, ActorId = actorId, OccurredAt = DateTimeOffset.UtcNow });

    private static object ToView(MonitorRun run)
    {
        var configuration = JsonSerializer.Deserialize<LoadConfiguration>(run.Configuration, Json);
        var snapshot = run.Latest is null ? null : JsonSerializer.Deserialize<LoadSnapshot>(run.Latest, Json);
        return new
        {
            run.Id, run.CreatedAt, run.StartedAt, run.FinishedAt, run.HeartbeatAt, run.Status, run.StopRequested,
            run.Active, run.Cleaned, configuration, snapshot,
            target = new { environment = "Docker Sandbox", webCpuLimit = 2, webMemoryLimitBytes = 768L * 1024 * 1024,
                databaseCpuLimit = 1, databaseMemoryLimitBytes = 768L * 1024 * 1024 },
            result = snapshot?.BenchmarkOutcome
        };
    }

    private static string BenchmarkResult(LoadConfiguration? config, LoadSnapshot snapshot)
    {
        if (config is null || config.Profile != "sandbox-capacity" ||
            config.Users != 60 || config.DurationMinutes != 15 || config.RampMinutes != 3 ||
            !config.SignalR || !config.DirectMessages || !config.GroupChat || config.MessagesPerMinute != 4 ||
            config.ReadsPerMinute != 8 || config.Reconnect || config.OfflineSync || config.Uploads)
            return "Inconclusive";

        if (!config.CommunityPosts || !config.Comments || config.PostsPerHour != 2 || config.CommentsPerHour != 6 ||
            config.Reactions || config.Typing) return "Inconclusive";

        var read = snapshot.Latencies.SingleOrDefault(x => x.Operation == "read");
        var direct = snapshot.Latencies.SingleOrDefault(x => x.Operation == "direct");
        var group = snapshot.Latencies.SingleOrDefault(x => x.Operation == "group");
        var delivery = snapshot.Latencies.SingleOrDefault(x => x.Operation == "delivery");
        if (read is not { Count: >= 100 } || direct is not { Count: >= 100 } || group is not { Count: >= 100 } ||
            delivery is not { Count: >= 100 } || snapshot.SteadySuccesses == 0) return "Inconclusive";

        var errorRate = snapshot.SteadyFailures / (double)(snapshot.SteadySuccesses + snapshot.SteadyFailures);
        const int expectedCoreOperations = 8_736;
        return snapshot.TargetReachedSeconds >= 684 && snapshot.SteadyWorkload >= expectedCoreOperations * .95 &&
            snapshot.SteadyThrottled == 0 && errorRate <= .005 && read.P95Ms <= 300 && direct.P95Ms <= 500 &&
            group.P95Ms <= 500 && delivery.P95Ms <= 1_000 && snapshot.Missing == 0 && snapshot.DuplicateMessages == 0
            ? "Pass" : "Fail";
    }

    private sealed record StartRequest(Guid RequestId, LoadConfiguration? Configuration);
}
