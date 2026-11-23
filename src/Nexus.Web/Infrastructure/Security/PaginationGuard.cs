namespace Nexus.Web.Infrastructure.Security;

public static class PaginationGuard
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = NexusSecurityOptions.HardMaxPageSize;

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
