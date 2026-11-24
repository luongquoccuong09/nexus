using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nexus.Monitoring;

namespace Nexus.LoadRunner;

internal sealed class DockerSandbox(string composeFile, string image, LoadLease lease)
{
    private const string FixturePrefix = "NEXUS_SANDBOX_FIXTURE ";
    private readonly string _project = "nexus-load-" + lease.Id.ToString("N");
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private string? _containerId;

    public async Task<SandboxTarget> StartAsync(CancellationToken ct)
    {
        var imageId = (await RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], ct)).Trim();
        if (imageId.Length != 71 || !imageId.StartsWith("sha256:", StringComparison.Ordinal) ||
            !imageId[7..].All(Uri.IsHexDigit))
            throw new RunnerFailure("image_unavailable");

        var commit = (await RunAsync(["image", "inspect", imageId, "--format",
            "{{index .Config.Labels \"org.opencontainers.image.revision\"}}"], ct)).Trim();
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit)) commit = "";
        _environment["NEXUS_SANDBOX_IMAGE"] = imageId;
        _environment["NEXUS_SANDBOX_COMMIT"] = commit;
        _environment["NEXUS_SANDBOX_USERS"] = lease.Configuration.Users.ToString(CultureInfo.InvariantCulture);
        var password = "Nexus!7a-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        _environment["NEXUS_SANDBOX_PASSWORD"] = password;
        if (lease.Configuration.Profile == "sandbox-capacity")
        {
            _environment["NEXUS_SANDBOX_RATE"] = "24000";
            _environment["NEXUS_SANDBOX_LOGIN_RATE"] = "600";
        }
        try
        {
            await ComposeAsync(["up", "--detach", "--pull", "never", "ingress"], ct);
            var data = await ComposeAsync(["run", "--rm", "--no-deps", "--pull", "never", "-T", "seed"], ct);
            var fixtureLine = data.Split('\n').SingleOrDefault(line => line.StartsWith(FixturePrefix, StringComparison.Ordinal));
            var fixture = fixtureLine is null ? null :
                JsonSerializer.Deserialize<SandboxFixture>(fixtureLine[FixturePrefix.Length..]);
            if (fixture is null || fixture.DirectIds.Length != lease.Configuration.Users ||
                fixture.UserIds.Length != Math.Max(2, lease.Configuration.Users))
                throw new RunnerFailure("invalid_seed_manifest");

            _containerId = (await ComposeAsync(["ps", "--quiet", "web"], ct)).Trim();
            if (_containerId.Length is < 12 or > 64 || !_containerId.All(Uri.IsHexDigit))
                throw new RunnerFailure("sandbox_container_missing");
            var portText = (await ComposeAsync(["port", "ingress", "8080"], ct)).Trim();
            if (!IPEndPoint.TryParse(portText, out var endpoint) || !endpoint.Address.Equals(IPAddress.Loopback) ||
                endpoint.Port < 1024)
                throw new RunnerFailure("unexpected_sandbox_binding");

            var url = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), endpoint.Port).Uri;
            using var client = RunnerHttp.Client(url, TimeSpan.FromSeconds(4));
            for (var attempt = 0; attempt < 30; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (await RunnerHttp.ReadyAsync(client, ct))
                {
                    var version = await client.GetFromJsonAsync<SandboxVersion>("/api/version", ct);
                    if (version is not null && Version.TryParse(version.Version, out _) &&
                        string.Equals(version.Commit ?? "", commit, StringComparison.Ordinal))
                        return new SandboxTarget(url, fixture, password, version.Version, version.Commit, imageId);
                    throw new RunnerFailure("unexpected_sandbox_build");
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            throw new RunnerFailure("sandbox_not_ready");
        }
        finally
        {
            _environment.Remove("NEXUS_SANDBOX_PASSWORD");
        }
    }

    public async Task<ContainerSample?> MeasureAsync(CancellationToken ct)
    {
        if (_containerId is null) return null;
        var output = await RunAsync(["stats", "--no-stream", "--format", "{{.CPUPerc}}|{{.MemUsage}}|{{.NetIO}}|{{.BlockIO}}",
            _containerId], ct);
        var fields = output.Trim().Split('|');
        if (fields.Length != 4 || !double.TryParse(fields[0].TrimEnd('%'), CultureInfo.InvariantCulture, out var cpu)) return null;
        var memory = ParsePair(fields[1]);
        var network = ParsePair(fields[2]);
        var io = ParsePair(fields[3]);
        return new(cpu, memory?.First, memory?.Second, network?.First, network?.Second, io?.First, io?.Second);
    }

    public async Task<bool> CleanAsync(CancellationToken ct)
    {
        // The compose project name is derived only from the acquired run's ID.
        _environment.TryAdd("NEXUS_SANDBOX_IMAGE", image);
        try
        {
            await ComposeAsync(["down", "--volumes", "--remove-orphans", "--timeout", "5"], ct);
            return true;
        }
        catch (Exception ex) when (ex is RunnerFailure or OperationCanceledException)
        {
            return false;
        }
    }

    private Task<string> ComposeAsync(string[] arguments, CancellationToken ct) =>
        RunAsync(["compose", "--file", composeFile, "--project-name", _project, .. arguments], ct);

    private async Task<string> RunAsync(string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in _environment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new RunnerFailure("docker_unavailable");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            await errors; // Docker may include environment or path details. Keep these out of runner reports.
            if (process.ExitCode != 0) throw new RunnerFailure($"docker_{arguments[0]}_failed");
            return await output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static (long First, long Second)? ParsePair(string field)
    {
        var values = field.Split('/', StringSplitOptions.TrimEntries);
        return values.Length == 2 && ParseBytes(values[0]) is { } first && ParseBytes(values[1]) is { } second
            ? (first, second) : null;
    }

    private static long? ParseBytes(string value)
    {
        var i = 0;
        while (i < value.Length && (value[i] == '.' || char.IsAsciiDigit(value[i]))) i++;
        if (!double.TryParse(value.AsSpan(0, i), CultureInfo.InvariantCulture, out var quantity)) return null;
        var unit = value[i..].Trim();
        var multiplier = unit switch
        {
            "B" => 1L, "kB" => 1_000L, "MB" => 1_000_000L, "GB" => 1_000_000_000L,
            "KiB" => 1_024L, "MiB" => 1_048_576L, "GiB" => 1_073_741_824L, _ => 0L
        };
        return multiplier == 0 || quantity * multiplier > long.MaxValue ? null : (long)(quantity * multiplier);
    }

    private sealed record SandboxVersion(string Version, string? Commit);
}

internal sealed record SandboxTarget(Uri Url, SandboxFixture Fixture, string Password, string Version, string? Commit, string ImageId);
internal sealed record ContainerSample(double CpuPercent, long? MemoryBytes, long? MemoryLimitBytes,
    long? NetworkInBytes, long? NetworkOutBytes, long? DiskReadBytes, long? DiskWriteBytes);
internal sealed class RunnerFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}
