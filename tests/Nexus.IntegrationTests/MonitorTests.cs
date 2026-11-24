using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Monitoring;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class MonitorTests(PostgresContainerFixture pg, ITestOutputHelper output) : IClassFixture<PostgresContainerFixture>
{
    private static readonly LoadConfiguration Smoke = new("sandbox-capacity", 10, 1, 0,
        6, 4, 12, 10, true, true, true, true, true, true, true, true, true, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Runs = "/api/monitor/runs";

    [Fact]
    public async Task Monitor_SeparatesAccountGrantsAndMachineLeaseFromOrganizationRoles()
    {
        var people = new People();
        await using var app = await StartAsync(people);
        using var anonymous = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/monitor/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/monitor/runner/claim", null)).StatusCode);

        using var organizationAdmin = await LoginAsync(app, people.Admin);
        Assert.False((await ReadAsync(organizationAdmin, "/api/auth/me")).GetProperty("canViewMonitor").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await organizationAdmin.GetAsync(Runs)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await organizationAdmin.GetAsync(Runs + "/" + Guid.NewGuid() + "/report")).StatusCode);

        using var viewer = await LoginAsync(app, people.Viewer);
        Assert.True((await ReadAsync(viewer, "/api/auth/me")).GetProperty("canViewMonitor").GetBoolean());
        Assert.False((await ReadAsync(viewer, "/api/monitor/capabilities")).GetProperty("canRun").GetBoolean());
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(viewer, Runs, new { requestId = id, configuration = Smoke })).StatusCode);

        using var operatorClient = await LoginAsync(app, people.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(operatorClient, Runs, new { requestId = id, configuration = Smoke })).StatusCode);
        await VerifyMfaAsync(operatorClient);
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PostAsJsonAsync(Runs, new { requestId = id, configuration = Smoke })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostAsync(operatorClient, Runs, new { requestId = id, configuration = Smoke with { Profile = "https://production.invalid" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(operatorClient, Runs, new { requestId = id, configuration = Smoke })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(operatorClient, Runs, new { requestId = id, configuration = Smoke })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(operatorClient, Runs, new { requestId = Guid.NewGuid(), configuration = Smoke })).StatusCode);

        using var machine = Client(app);
        machine.DefaultRequestHeaders.Add("X-Nexus-Runner-Key", people.Key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.GetAsync("/api/monitor/capabilities")).StatusCode);
        using var claim = await machine.PostAsync("/api/monitor/runner/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var lease = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lease").Deserialize<LoadLease>(Json)!;
        Assert.Equal(id, lease.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await machine.PostAsync("/api/monitor/runner/claim", null)).StatusCode);

        var snapshot = EmptySnapshot();
        var path = $"/api/monitor/runner/runs/{id}";
        Assert.Equal(HttpStatusCode.NotFound, (await machine.PostAsJsonAsync(path + "/heartbeat", new RunnerUpdate(Guid.NewGuid(), snapshot))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(operatorClient, Runs + $"/{id}/stop", null)).StatusCode);
        using var heartbeat = await machine.PostAsJsonAsync(path + "/heartbeat", new RunnerUpdate(lease.Generation, snapshot));
        Assert.True((await heartbeat.Content.ReadFromJsonAsync<HeartbeatReply>())!.StopRequested);
        var final = new RunnerResult(lease.Generation, "stopped", true, snapshot with { Phase = "stopped" });
        Assert.Equal(HttpStatusCode.NoContent, (await machine.PostAsJsonAsync(path + "/result", final)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await machine.PostAsJsonAsync(path + "/result", final)).StatusCode);
        var report = await ReadAsync(viewer, Runs + $"/{id}/report?format=json");
        Assert.Equal("stopped", report.GetProperty("status").GetString());
        Assert.True(report.GetProperty("cleaned").GetBoolean());
        Assert.False(report.TryGetProperty("actorId", out _));

        await using var scope = app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var account = (await manager.FindByIdAsync(people.Viewer.ToString()))!;
        account.Status = AccountStatus.Disabled;
        Assert.True((await manager.UpdateAsync(account)).Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Runs)).StatusCode);
    }

    [MonitorImageFact]
    public async Task Monitor_ActualDockerWorkerRunsStopsAndRemovesSandbox()
    {
        var people = new People();
        await using var app = await StartAsync(people);
        using var client = await LoginAsync(app, people.Operator);
        await VerifyMfaAsync(client);

        var root = RepoRoot();
        var mode = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(root, "src", "Nexus.LoadRunner", "bin", mode, "net10.0", "Nexus.LoadRunner.dll");
        Assert.True(File.Exists(dll), "Build Nexus.slnx before the Docker smoke test.");
        var state = Directory.CreateTempSubdirectory("nexus-monitor-smoke-");
        var processInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true
        };
        processInfo.ArgumentList.Add(dll);
        processInfo.Environment["NEXUS_RUNNER_CONTROL_URL"] = client.BaseAddress!.AbsoluteUri;
        processInfo.Environment["NEXUS_RUNNER_KEY"] = people.Key;
        processInfo.Environment["NEXUS_RUNNER_IMAGE"] = Environment.GetEnvironmentVariable("NEXUS_MONITOR_SMOKE_IMAGE")!;
        processInfo.Environment["NEXUS_RUNNER_STATE_DIR"] = state.FullName;
        using var worker = Process.Start(processInfo)!;
        var offlineCleanupVerified = false;
        try
        {
            var id = Guid.NewGuid();
            var baseline = Environment.GetEnvironmentVariable("NEXUS_MONITOR_FULL_BASELINE") == "1";
            var config = baseline ? Smoke with { Users = 60, DurationMinutes = 15, RampMinutes = 3,
                MessagesPerMinute = 4, ReadsPerMinute = 8, PostsPerHour = 2, CommentsPerHour = 6,
                Reactions = false, Uploads = false, Typing = false, Reconnect = false, OfflineSync = false } : Smoke;
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, Runs, new { requestId = id, configuration = config })).StatusCode);
            var live = await WaitForRunAsync(client, id, run => !run.GetProperty("active").GetBoolean() ||
                run.GetProperty("snapshot") is { ValueKind: JsonValueKind.Object } sample &&
                sample.GetProperty("sockets").GetInt32() >= Math.Min(10, config.Users), TimeSpan.FromMinutes(5));
            Assert.True(live.GetProperty("active").GetBoolean(), live.GetRawText());
            Assert.True((await ReadAsync(client, Runs + $"/{id}/performance")).GetProperty("samples").GetArrayLength() > 0);
            var final = await WaitForRunAsync(client, id, run => !run.GetProperty("active").GetBoolean(), TimeSpan.FromMinutes(config.DurationMinutes + 2));
            Assert.True(final.GetProperty("status").GetString() == "completed", final.GetRawText());
            Assert.True(final.GetProperty("cleaned").GetBoolean());
            var snapshot = final.GetProperty("snapshot");
            Assert.True(snapshot.GetProperty("messages").GetInt64() > 0, final.GetRawText());
            Assert.True(snapshot.GetProperty("delivered").GetInt64() > 0, final.GetRawText());
            if (!baseline)
            {
                Assert.True(snapshot.GetProperty("uploads").GetInt64() > 0, final.GetRawText());
                Assert.True(snapshot.GetProperty("reconnects").GetInt64() > 0, final.GetRawText());
                Assert.Equal(0, snapshot.GetProperty("syncFailures").GetInt64());
                Assert.Equal(0, snapshot.GetProperty("duplicateMessages").GetInt64());
            }
            Assert.Equal(0, snapshot.GetProperty("missing").GetInt64());
            Assert.True(snapshot.GetProperty("peakMemoryBytes").GetInt64() > 0);
            output.WriteLine("Verified sandbox report: " + final.GetRawText());
            using var html = await client.GetAsync(Runs + $"/{id}/report?format=html");
            Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Nexus Load Test Report", await html.Content.ReadAsStringAsync());
            await AssertRemovedAsync(id);

            var stopId = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, Runs, new { requestId = stopId, configuration = Smoke })).StatusCode);
            var running = await WaitForRunAsync(client, stopId, run => !run.GetProperty("active").GetBoolean() ||
                run.GetProperty("snapshot") is { ValueKind: JsonValueKind.Object } sample && sample.GetProperty("sockets").GetInt32() > 0,
                TimeSpan.FromMinutes(5));
            Assert.True(running.GetProperty("active").GetBoolean(), running.GetRawText());
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, Runs + $"/{stopId}/stop", null)).StatusCode);
            var stopped = await WaitForRunAsync(client, stopId, run => !run.GetProperty("active").GetBoolean(), TimeSpan.FromMinutes(1));
            Assert.True(stopped.GetProperty("status").GetString() == "stopped", stopped.GetRawText());
            Assert.True(stopped.GetProperty("cleaned").GetBoolean());
            await AssertRemovedAsync(stopId);

            var offlineId = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, Runs, new { requestId = offlineId, configuration = Smoke })).StatusCode);
            var online = await WaitForRunAsync(client, offlineId, run => !run.GetProperty("active").GetBoolean() ||
                run.GetProperty("snapshot") is { ValueKind: JsonValueKind.Object } sample && sample.GetProperty("sockets").GetInt32() > 0,
                TimeSpan.FromMinutes(5));
            Assert.True(online.GetProperty("active").GetBoolean(), online.GetRawText());
            await app.StopAsync();
            await VerifyOfflineCleanupAsync(Path.Combine(state.FullName, "run.json"), offlineId);
            offlineCleanupVerified = true;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(state.FullName, "emergency-stop"), "");
            if (!worker.HasExited)
            {
                for (var wait = 0; wait < 25 && !offlineCleanupVerified && File.Exists(Path.Combine(state.FullName, "run.json")); wait++)
                    await Task.Delay(1000);
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            if (offlineCleanupVerified || !File.Exists(Path.Combine(state.FullName, "run.json"))) state.Delete(recursive: true);
        }
    }

    private async Task<WebApplication> StartAsync(People people)
    {
        var app = NexusHost.Build([
            "--environment=Development", "--urls=http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}", "--NexusMonitor:Enabled=true",
            "--NexusMonitor:EnvironmentName=Test", $"--NexusMonitor:Readers:0={people.Viewer}",
            $"--NexusMonitor:Operators:0={people.Operator}", $"--NexusMonitor:RunnerKey={people.Key}"
        ]);
        await using var scope = app.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var userNames = new[] { people.Operator, people.Viewer, people.Admin };
        foreach (var id in userNames)
        {
            var account = new NexusUser { Id = id, UserName = Name(id), DisplayName = "Monitor test account" };
            Assert.True((await manager.CreateAsync(account, Password)).Succeeded);
            if (id != people.Admin) continue;
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
            var organization = await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, options);
            OrganizationFixtureHandler.Add(db, organization.Id, account, OrgRole.SystemAdmin, TrustLevel.Verified);
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpClient> LoginAsync(WebApplication app, Guid id)
    {
        var client = Client(app);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = Name(id), password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static HttpClient Client(WebApplication app) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true
    })
    {
        BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static async Task VerifyMfaAsync(HttpClient client)
    {
        using var setup = await client.PostAsync("/api/auth/mfa/setup", null);
        setup.EnsureSuccessStatusCode();
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        using var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = IdentityM1aTests.GenerateTotpCode(key) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? payload)
    {
        var csrf = (await ReadAsync(client, "/api/auth/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> WaitForRunAsync(HttpClient client, Guid id, Func<JsonElement, bool> predicate, TimeSpan limit)
    {
        var started = Stopwatch.GetTimestamp();
        JsonElement last = default;
        while (Stopwatch.GetElapsedTime(started) < limit)
        {
            last = (await ReadAsync(client, Runs + $"/{id}")).GetProperty("run");
            if (predicate(last)) return last;
            await Task.Delay(3000);
        }
        Assert.Fail($"Expected run state was not reached: {last}");
        return last;
    }

    private static async Task VerifyOfflineCleanupAsync(string path, Guid runId)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(75))
        {
            JsonElement state;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                state = await JsonSerializer.DeserializeAsync<JsonElement>(stream);
            }
            if (state.GetProperty("result") is { ValueKind: JsonValueKind.Object } result)
            {
                Assert.Equal("safety_stop", result.GetProperty("outcome").GetString());
                Assert.Equal("control_lease_expired", result.GetProperty("snapshot").GetProperty("reason").GetString());
                Assert.True(result.GetProperty("cleaned").GetBoolean());
                await AssertRemovedAsync(runId);
                return;
            }
            await Task.Delay(2000);
        }
        Assert.Fail("The worker did not clean up after the control lease expired.");
    }

    private static async Task AssertRemovedAsync(Guid id)
    {
        var project = $"label=com.docker.compose.project=nexus-load-{id:N}";
        foreach (var args in new string[][] { ["ps", "-a", "-q"], ["volume", "ls", "-q"], ["network", "ls", "-q"] })
        {
            var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add(project);
            using var docker = Process.Start(start)!;
            Assert.Equal("", (await docker.StandardOutput.ReadToEndAsync()).Trim());
            await docker.WaitForExitAsync();
            Assert.Equal(0, docker.ExitCode);
        }
    }

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Nexus.slnx"))) return path.FullName;
        throw new InvalidOperationException("Could not locate Nexus.slnx.");
    }

    private static string Name(Guid id) => $"monitor_{id:N}"[..25];
    private const string Password = "Monitor_Integration_83!";
    private static LoadSnapshot EmptySnapshot() => new("provisioning", 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, []);

    private sealed class People
    {
        public Guid Operator { get; } = Guid.NewGuid();
        public Guid Viewer { get; } = Guid.NewGuid();
        public Guid Admin { get; } = Guid.NewGuid();
        public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }
}

public sealed class MonitorImageFactAttribute : FactAttribute
{
    public MonitorImageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_MONITOR_SMOKE_IMAGE")))
            Skip = "Set NEXUS_MONITOR_SMOKE_IMAGE to a local Nexus Docker image to exercise the independent runner.";
    }
}
