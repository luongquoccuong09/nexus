using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Nexus.Web.Infrastructure.Security;

public static class PaginationGuard
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = NexusSecurityOptions.HardMaxPageSize;

    public sealed record NameCursor(string Name, Guid Id);

    public static string EncodeCursor<T>(T key) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(key));

    public static bool TryDecodeCursor<T>(string? cursor, out T? key) where T : class
    {
        key = null;
        if (cursor is null) return true;
        if (cursor.Length is 0 or > 4096) return false;
        try
        {
            key = JsonSerializer.Deserialize<T>(WebEncoders.Base64UrlDecode(cursor));
            return key is not null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    public static IResult InvalidCursor() =>
        Results.BadRequest(new { code = "invalid_cursor", message = "The page cursor is invalid." });

    public static IResult CursorPage<T>(List<T> rows, int pageSize, Func<T, Guid> key)
    {
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        return Results.Ok(new { items = rows, nextAfter = hasMore ? key(rows[^1]) : (Guid?)null });
    }

    public static bool TryNormalize(int? requested, out int pageSize, out string? error)
    {
        if (!requested.HasValue)
        {
            pageSize = DefaultPageSize;
            error = null;
            return true;
        }

        if (requested.Value < 1 || requested.Value > MaxPageSize)
        {
            pageSize = 0;
            error = $"Page size must be between 1 and {MaxPageSize}.";
            return false;
        }

        pageSize = requested.Value;
        error = null;
        return true;
    }
}
