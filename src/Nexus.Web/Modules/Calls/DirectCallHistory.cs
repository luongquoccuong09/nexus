using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Meetings;

namespace Nexus.Web.Modules.Calls;

internal static class DirectCallHistory
{
    internal static async Task<IResult> ListAsync(HttpContext http, string? filter, int? limit, string? cursor,
        [FromServices] NexusDbContext db, MeetingsAccess access, IMeetingProvider provider, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.User.TryGetUserId(out var userId) || http.User.GetSessionId() is not { } sessionId ||
            !await access.IsActiveSessionAsync(userId, sessionId, ct))
            return Results.Unauthorized();

        filter ??= "all";
        if (filter is not ("all" or "incoming" or "outgoing" or "missed"))
            return Results.BadRequest(new { code = "invalid_call_history_filter", message = "Choose all, incoming, outgoing, or missed calls." });
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        if (!PaginationGuard.TryDecodeCursor<HistoryCursor>(cursor, out var after) ||
            after is not null && (after.CreatedAt == default || after.Id == Guid.Empty || after.Filter != filter))
            return PaginationGuard.InvalidCursor();

        var history = DirectCallAccess.Readable(db).Where(call => call.EndedAt != null);
        history = filter switch
        {
            "incoming" => history.Where(call => call.CalleeId == userId),
            "outgoing" => history.Where(call => call.CallerId == userId),
            "missed" => history.Where(call => call.CalleeId == userId && call.State == DirectCallState.Missed),
            _ => history.Where(call => call.CallerId == userId || call.CalleeId == userId)
        };
        if (after is not null)
        {
            var cutoff = after.CreatedAt.ToUniversalTime();
            history = history.Where(call => call.CreatedAt < cutoff ||
                call.CreatedAt == cutoff && call.Id.CompareTo(after.Id) < 0);
        }

        var page = history.OrderByDescending(call => call.CreatedAt).ThenByDescending(call => call.Id).Take(pageSize + 1);
        var unblocked = DirectCallAccess.Unblocked(db, db.DirectCalls.AsNoTracking());
        var providerAvailable = provider.Enabled;
        var rows = await (
            from call in page
            let outgoing = call.CallerId == userId
            join counterpart in db.Users.AsNoTracking() on (outgoing ? call.CalleeId : call.CallerId) equals counterpart.Id
            orderby call.CreatedAt descending, call.Id descending
            select new
            {
                call.Id, call.OrganizationId, call.ConversationId, call.Kind, call.State, Outgoing = outgoing,
                CounterpartId = counterpart.Id, CounterpartName = counterpart.DisplayName,
                call.CreatedAt, call.AcceptedAt, call.EndedAt,
                CanCallBack = providerAvailable && unblocked.Any(candidate => candidate.Id == call.Id)
            }).ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        return Results.Ok(new
        {
            items = rows.Select(row => new
            {
                row.Id, row.OrganizationId, row.ConversationId, Kind = row.Kind.ToString().ToLowerInvariant(),
                State = DirectCallLifecycle.WireState(row.State), Direction = row.Outgoing ? "outgoing" : "incoming",
                row.CounterpartId, row.CounterpartName, row.CreatedAt, row.AcceptedAt, row.EndedAt,
                DurationSeconds = row.AcceptedAt is { } accepted && row.EndedAt is { } ended
                    ? (long?)Math.Max(0, (ended - accepted).Ticks / TimeSpan.TicksPerSecond)
                    : null,
                row.CanCallBack
            }),
            nextCursor = hasMore ? PaginationGuard.EncodeCursor(new HistoryCursor(rows[^1].CreatedAt, rows[^1].Id, filter)) : null
        });
    }

    private sealed record HistoryCursor(DateTimeOffset CreatedAt, Guid Id, string Filter);
}
