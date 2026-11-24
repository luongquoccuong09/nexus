namespace Nexus.LoadRunner;

internal sealed record RunnerSettings(Uri Control, string Key, string ComposeFile, string Image, string StateDirectory)
{
    public static RunnerSettings FromEnvironment()
    {
        var address = Environment.GetEnvironmentVariable("NEXUS_RUNNER_CONTROL_URL");
        var key = Environment.GetEnvironmentVariable("NEXUS_RUNNER_KEY");
        var compose = Environment.GetEnvironmentVariable("NEXUS_RUNNER_COMPOSE_FILE") ??
            Path.Combine("deploy", "docker-compose.monitor.yml");
        var image = Environment.GetEnvironmentVariable("NEXUS_RUNNER_IMAGE");
        var state = Environment.GetEnvironmentVariable("NEXUS_RUNNER_STATE_DIR") ??
            Path.Combine("artifacts", "monitor-runner");

        if (!Uri.TryCreate(address, UriKind.Absolute, out var control) ||
            control.Scheme != Uri.UriSchemeHttps && !(control.Scheme == Uri.UriSchemeHttp && control.IsLoopback) ||
            control.AbsolutePath != "/" || control.UserInfo != "" || control.Query != "" || control.Fragment != "")
            throw new RunnerFailure("invalid_control_origin");
        if (key is not { Length: >= 32 and <= 128 } ||
            !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new RunnerFailure("invalid_runner_key");
        if (image is not { Length: >= 1 and <= 200 } || !char.IsAsciiLetterOrDigit(image[0]) ||
            !image.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' or ':' or '.' or '@'))
            throw new RunnerFailure("invalid_sandbox_image");
        if (!File.Exists(compose)) throw new RunnerFailure("sandbox_compose_file_missing");
        return new(control, key, Path.GetFullPath(compose), image, Path.GetFullPath(state));
    }
}
