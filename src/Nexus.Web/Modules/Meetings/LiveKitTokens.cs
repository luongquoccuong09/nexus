using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Nexus.Web.Modules.Meetings;

internal sealed class LiveKitTokens(IOptions<LiveKitOptions> settings)
{
    private static readonly string Header = EncodeJson(new { alg = "HS256", typ = "JWT" });
    private readonly LiveKitOptions _settings = settings.Value;
    private readonly byte[] _signingKey = Encoding.UTF8.GetBytes(settings.Value.ApiSecret);

    internal string Join(string roomName, Guid grantId, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return Sign(new
        {
            iss = _settings.ApiKey,
            sub = grantId.ToString("N"),
            name = displayName,
            nbf = now.ToUnixTimeSeconds(),
            exp = now.AddSeconds(60).ToUnixTimeSeconds(),
            video = new { room = roomName, roomJoin = true, canPublish = true, canSubscribe = true, canPublishData = false }
        });
    }

    internal string Remove(string roomName) => Admin(new { room = roomName, roomAdmin = true });

    internal string Delete() => Admin(new { roomCreate = true });

    internal bool TryReadJoin(string token, out Guid grantId, out string roomName)
    {
        grantId = Guid.Empty;
        roomName = string.Empty;
        if (!_settings.Enabled || token.Length is 0 or > 8192) return false;

        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return false;

        try
        {
            var hash = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(hash, WebEncoders.Base64UrlDecode(parts[2]))) return false;

            using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0]));
            if (ReadString(header.RootElement, "alg") != "HS256") return false;

            using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var root = claims.RootElement;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ReadString(root, "iss") != _settings.ApiKey ||
                !Guid.TryParseExact(ReadString(root, "sub"), "N", out grantId) || grantId == Guid.Empty ||
                !ReadUnixTime(root, "exp", out var expiry) || expiry <= now ||
                root.TryGetProperty("nbf", out _) && (!ReadUnixTime(root, "nbf", out var notBefore) || notBefore > now + 2) ||
                !root.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object ||
                !IsTrue(video, "roomJoin") || IsTrue(video, "roomAdmin") || IsTrue(video, "roomCreate"))
            {
                return false;
            }

            roomName = ReadString(video, "room") ?? string.Empty;
            return roomName.Length is > 0 and <= 128;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private string Admin<T>(T video)
    {
        var now = DateTimeOffset.UtcNow;
        return Sign(new { iss = _settings.ApiKey, nbf = now.ToUnixTimeSeconds(), exp = now.AddSeconds(15).ToUnixTimeSeconds(), video });
    }

    private string Sign<T>(T claims)
    {
        var content = $"{Header}.{EncodeJson(claims)}";
        var signature = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(content));
        return $"{content}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    private static string EncodeJson<T>(T value) => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));

    private static string? ReadString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadUnixTime(JsonElement element, string key, out long result)
    {
        result = 0;
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result);
    }

    private static bool IsTrue(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
