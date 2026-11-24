using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nexus.Monitoring;

namespace Nexus.LoadRunner;

internal sealed class LoadWorker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RunnerSettings _settings;
    private readonly HttpClient _control;
    private readonly string _statePath;
    private readonly string _stopPath;

    public LoadWorker(RunnerSettings settings)
    {
        _settings = settings;
        _control = RunnerHttp.Client(settings.Control, TimeSpan.FromSeconds(5));
        _control.DefaultRequestHeaders.Add("X-Nexus-Runner-Key", settings.Key);
        Directory.CreateDirectory(settings.StateDirectory);
        _statePath = Path.Combine(settings.StateDirectory, "run.json");
        _stopPath = Path.Combine(settings.StateDirectory, "emergency-stop");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var instance = new FileStream(Path.Combine(_settings.StateDirectory, "runner.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("Nexus Load Runner started.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_statePath))
                {
                    await RecoverAsync(ct);
                }
                else if (!File.Exists(_stopPath))
                {
                    using var response = await _control.PostAsync("/api/monitor/runner/claim", null, ct);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var command = await response.Content.ReadFromJsonAsync<Command>(ct);
                        if (command is not { Lease: not null, LeaseSeconds: >= 15 and <= 120 } ||
                            command.Lease.Id == Guid.Empty || command.Lease.Generation == Guid.Empty)
                            throw new RunnerFailure("invalid_runner_lease");
                        await SaveAsync(new(command.Lease, null));
                        await ExecuteAsync(command, ct);
                    }
                }
            }
            catch (Exception ex) when (RunnerHttp.IsTransient(ex, ct) || ex is JsonException or RunnerFailure)
            {
                // Incoming and outgoing payloads are intentionally absent from console and report logs.
                Console.WriteLine("Runner coordination temporarily unavailable.");
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task ExecuteAsync(Command command, CancellationToken shuttingDown)
    {
        var lease = command.Lease;
        var metrics = new RunMeasurements(lease.Configuration);
        var sandbox = Sandbox(lease);
        var outcome = "completed";
        string? reason = null;
        string? stopReason = null;
        var currentPhase = "provisioning";
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(shuttingDown);
        using var controlPolling = new CancellationTokenSource();
        using var leaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(command.LeaseSeconds));
        using var active = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, leaseDeadline.Token);
        var polling = RenewAsync();
        try
        {
            if (!lease.Configuration.IsValid) throw new RunnerFailure("invalid_configuration");
            using var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
            setupDeadline.CancelAfter(TimeSpan.FromMinutes(5));
            SandboxTarget target;
            try { target = await sandbox.StartAsync(setupDeadline.Token); }
            catch (OperationCanceledException) when (!active.IsCancellationRequested)
            {
                throw new RunnerFailure("provisioning_deadline");
            }
            using var health = RunnerHttp.Client(target.Url, TimeSpan.FromSeconds(3));
            metrics.Start(target);
            Volatile.Write(ref currentPhase, lease.Configuration.RampMinutes > 0 ? "ramp" : "steady");
            using var workloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
            workloadDeadline.CancelAfter(TimeSpan.FromMinutes(lease.Configuration.DurationMinutes));

            var users = Enumerable.Range(0, lease.Configuration.Users)
                .Select(index => new VirtualUser(index, target, metrics)).ToArray();
            var traffic = Task.WhenAll(users.Select(user => RunUserAsync(user, workloadDeadline.Token)));
            var probes = ObserveTargetAsync(sandbox, health, metrics, SetStop, workloadDeadline.Token);
            try
            {
                await Task.WhenAny(traffic, probes);
                if (probes.IsFaulted) await probes;
                if (traffic.IsFaulted) await traffic;
                if (!workloadDeadline.IsCancellationRequested && traffic.IsCompleted)
                    SetStop("target_unhealthy");
            }
            finally
            {
                await workloadDeadline.CancelAsync();
                try
                {
                    await Task.WhenAll(traffic, probes);
                    metrics.EndMeasurement();
                    if (!active.IsCancellationRequested)
                    {
                        using var drain = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                        drain.CancelAfter(TimeSpan.FromSeconds(15));
                        await Task.Delay(TimeSpan.FromSeconds(5), drain.Token);
                        await metrics.Deliveries.ReconcileAsync(metrics.Connected, 100, drain.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    metrics.Deliveries.Finish();
                    await Task.WhenAll(users.Select(user => user.DisposeAsync().AsTask()));
                }
            }

            active.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            reason = Volatile.Read(ref stopReason) ?? (leaseDeadline.IsCancellationRequested ? "control_lease_expired" : "runner_interrupted");
            outcome = reason is "operator_stop" or "emergency_stop" or "runner_interrupted" ? "stopped" : "safety_stop";
        }
        catch (Exception ex) when (ex is RunnerFailure or IOException or System.ComponentModel.Win32Exception or
            HttpRequestException or InvalidOperationException or JsonException)
        {
            reason = ex is RunnerFailure failure && LoadSnapshot.Reasons.Contains(failure.Code) ? failure.Code : "runner_failed";
            outcome = "failed";
        }
        finally
        {
            Volatile.Write(ref currentPhase, "cleanup");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            bool cleaned;
            try { cleaned = await sandbox.CleanAsync(cleanup.Token); }
            catch (System.ComponentModel.Win32Exception) { cleaned = false; }
            await controlPolling.CancelAsync();
            await polling;
            var final = new RunnerResult(lease.Generation, outcome, cleaned, metrics.Snapshot(outcome, reason));
            await SaveAsync(new(lease, final));
            Console.WriteLine($"Run {lease.Id:N}: {outcome}{(reason is null ? "" : $" ({reason})")}.");
            using var submit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await SubmitAsync(lease, final, submit.Token);
        }

        void SetStop(string code)
        {
            if (Interlocked.CompareExchange(ref stopReason, code, null) is null) stopped.Cancel();
        }

        async Task RenewAsync()
        {
            var ct = controlPolling.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (File.Exists(_stopPath)) SetStop("emergency_stop");
                    var phase = Volatile.Read(ref currentPhase);
                    if (phase == "ramp" && metrics.Steady) phase = "steady";
                    try
                    {
                        using var reply = await _control.PostAsJsonAsync($"/api/monitor/runner/runs/{lease.Id}/heartbeat",
                            new RunnerUpdate(lease.Generation, metrics.Snapshot(phase, Volatile.Read(ref stopReason))), ct);
                        if (reply.IsSuccessStatusCode)
                        {
                            var update = await reply.Content.ReadFromJsonAsync<HeartbeatReply>(ct);
                            if (update is not null)
                            {
                                if (update.StopRequested) SetStop("operator_stop");
                                else leaseDeadline.CancelAfter(TimeSpan.FromSeconds(command.LeaseSeconds));
                            }
                        }
                        else if (reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                        {
                            SetStop("control_lease_expired");
                        }
                    }
                    catch (Exception ex) when (RunnerHttp.IsTransient(ex, ct) || ex is JsonException) { }
                    await Task.Delay(PollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private static async Task RunUserAsync(VirtualUser user, CancellationToken ct)
    {
        try { await user.RunAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task ObserveTargetAsync(DockerSandbox sandbox, HttpClient health, RunMeasurements metrics,
        Action<string> stop, CancellationToken ct)
    {
        var badHealth = 0;
        var badMemory = 0;
        var badRequests = 0;
        LoadSnapshot? previous = null;
        var previousTimestamp = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeLimit.CancelAfter(TimeSpan.FromSeconds(4));
                ContainerSample? container = null;
                try { container = await sandbox.MeasureAsync(timeLimit.Token); }
                catch (Exception ex) when (ex is RunnerFailure || RunnerHttp.IsTransient(ex, ct)) { }
                var now = Stopwatch.GetTimestamp();
                metrics.Sample(container, (int)Math.Round(Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds));
                previousTimestamp = now;
                badHealth = await RunnerHttp.ReadyAsync(health, ct) ? 0 : badHealth + 1;
                badMemory = container is { MemoryBytes: { } memory, MemoryLimitBytes: > 0 } &&
                    memory >= container.MemoryLimitBytes * .95 ? badMemory + 1 : 0;
                var current = metrics.Snapshot(metrics.Steady ? "steady" : "ramp");
                if (previous is not null)
                {
                    var failures = current.Failures - previous.Failures;
                    var rateLimited = current.Throttled - previous.Throttled;
                    var total = failures + current.Successes - previous.Successes;
                    badRequests = total >= 20 && failures - rateLimited >= total * .35 ? badRequests + 1 : 0;
                }
                previous = current;
                if (badHealth >= 3) stop("target_unhealthy");
                if (badMemory >= 3) stop("memory_limit");
                if (badRequests >= 3) stop("error_budget");
                using var reconciliation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reconciliation.CancelAfter(TimeSpan.FromSeconds(2));
                try { await metrics.Deliveries.ReconcileAsync(metrics.Connected, 24, reconciliation.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var file = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync<Spool>(file, Json, ct);
        file.Close();
        if (state is null) throw new RunnerFailure("invalid_local_state");
        var result = state.Result;
        if (result is not { Cleaned: true })
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cleanup.CancelAfter(TimeSpan.FromSeconds(45));
            var cleaned = await Sandbox(state.Lease).CleanAsync(cleanup.Token);
            result = result is null
                ? new(state.Lease.Generation, "stopped", cleaned,
                    new RunMeasurements(state.Lease.Configuration).Snapshot("stopped", "runner_interrupted"))
                : result with { Cleaned = cleaned };
            await SaveAsync(state with { Result = result });
        }
        await SubmitAsync(state.Lease, result, ct);
    }

    private async Task SubmitAsync(LoadLease lease, RunnerResult result, CancellationToken ct)
    {
        try
        {
            using var response = await _control.PostAsJsonAsync($"/api/monitor/runner/runs/{lease.Id}/result", result, ct);
            if (result.Cleaned && response.IsSuccessStatusCode) File.Delete(_statePath);
        }
        catch (Exception ex) when (RunnerHttp.IsTransient(ex, ct) || ex is OperationCanceledException) { }
    }

    private async Task SaveAsync(Spool state)
    {
        var temp = _statePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Json));
        // Windows can temporarily deny replacement while a reader or scanner has the file open.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, _statePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(250);
            }
        }
    }

    private DockerSandbox Sandbox(LoadLease lease) => new(_settings.ComposeFile, _settings.Image, lease);

    public void Dispose() => _control.Dispose();

    private sealed record Command(LoadLease Lease, int LeaseSeconds);
    private sealed record Spool(LoadLease Lease, RunnerResult? Result);
}
