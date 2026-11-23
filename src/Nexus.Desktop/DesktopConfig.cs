using System.Text.Json;
using Microsoft.Win32;

namespace Nexus.Desktop;

public sealed class DesktopUserConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;
    public bool IsMaximized { get; set; }
}

public sealed class BundledDesktopConfig
{
    public string DefaultServerUrl { get; set; } = string.Empty;
}

public static class DesktopConfigStore
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ProjectNexus";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string UserDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectNexus");

    public static string WebView2DataDir => Path.Combine(UserDataRoot, "WebView2");

    public static string UserConfigPath => Path.Combine(UserDataRoot, "config.json");

    public static string BundledConfigPath => Path.Combine(AppContext.BaseDirectory, "desktop-config.json");

    public static DesktopUserConfig Load()
    {
        Directory.CreateDirectory(UserDataRoot);

        var config = new DesktopUserConfig();
        if (File.Exists(UserConfigPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<DesktopUserConfig>(File.ReadAllText(UserConfigPath), JsonOptions);
                if (loaded is not null)
                {
                    config = loaded;
                }
            }
            catch
            {
                // Fall back to defaults if local config file is corrupted.
            }
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            var bundled = LoadBundledDefaultUrl();
            if (!string.IsNullOrWhiteSpace(bundled))
            {
                config.ServerUrl = bundled;
            }
        }

        return config;
    }

    public static void Save(DesktopUserConfig config)
    {
        Directory.CreateDirectory(UserDataRoot);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = UserConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, UserConfigPath, overwrite: true);
    }

    public static string LoadBundledDefaultUrl()
    {
        if (!File.Exists(BundledConfigPath))
        {
            return string.Empty;
        }

        try
        {
            var bundled = JsonSerializer.Deserialize<BundledDesktopConfig>(File.ReadAllText(BundledConfigPath), JsonOptions);
            return TryNormalizeServerUrl(bundled?.DefaultServerUrl, out var normalized, out _)
                ? normalized
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryNormalizeServerUrl(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var candidate = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            error = "Enter your Project Nexus server URL (for example: https://nexus.company.com).";
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = "The URL format is invalid.";
            return false;
        }

        var isLoopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                         uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            normalized = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && isLoopback)
        {
            normalized = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        error = "Only HTTPS URLs are allowed for remote servers (HTTP is allowed only for localhost / 127.0.0.1).";
        return false;
    }

    public static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var existing = key?.GetValue(RunValueName) as string;
            return !string.IsNullOrWhiteSpace(existing);
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue(RunValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}
