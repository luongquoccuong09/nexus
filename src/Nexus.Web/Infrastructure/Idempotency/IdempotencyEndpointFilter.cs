using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Npgsql;

namespace Nexus.Web.Infrastructure.Idempotency;

/// <summary>
/// Enforces the P0.3 / M0.5 Idempotency-Key contract documented in docs/SYNC_PROTOCOL.md.
/// </summary>
public sealed class IdempotencyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotency-Replayed";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var ct = httpContext.RequestAborted;

        if (!httpContext.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var headerValues) ||
            !IsValidIdempotencyKey(headerValues.ToString(), out var key))
        {
            return Results.BadRequest(new
            {
                code = "invalid_idempotency_key",
                message = "Header 'Idempotency-Key' must be 1 to 128 printable ASCII characters."
            });
        }

        var db = httpContext.RequestServices.GetRequiredService<NexusDbContext>();
        var httpMethod = httpContext.Request.Method.ToUpperInvariant();
        var routeTemplate = ResolveRouteTemplate(httpContext);
        if (db.OrganizationScopeId != Guid.Empty) routeTemplate = $"{db.OrganizationScopeId:N}:{routeTemplate}";
        var requestHash = await ComputeRequestBodyHashAsync(httpContext.Request, ct);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.IdempotencyKeys
            .SingleOrDefaultAsync(
                x => x.UserId == userId &&
                     x.HttpMethod == httpMethod &&
                     x.RouteTemplate == routeTemplate &&
                     x.IdempotencyKey == key,
                ct);

        if (existing is not null)
        {
            if (existing.ExpiresAt <= now)
            {
                db.IdempotencyKeys.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new { code = "idempotency_key_conflict" });
                }

                if (existing.CompletedAt is null)
                {
                    httpContext.Response.Headers.RetryAfter = "1";
                    return Results.Conflict(new { code = "idempotency_in_progress" });
                }

                httpContext.Response.Headers[ReplayedHeaderName] = "true";
                return Results.Text(
                    existing.ResponseJson ?? "{}",
                    "application/json; charset=utf-8",
                    Encoding.UTF8,
                    existing.StatusCode ?? StatusCodes.Status200OK);
            }
        }

        var record = new IdempotencyKeyEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            HttpMethod = httpMethod,
            RouteTemplate = routeTemplate,
            IdempotencyKey = key,
            RequestHash = requestHash,
            CreatedAt = now,
            ExpiresAt = now.Add(DefaultTtl)
        };

        db.IdempotencyKeys.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(record).State = EntityState.Detached;
            httpContext.Response.Headers.RetryAfter = "1";
            return Results.Conflict(new { code = "idempotency_in_progress" });
        }

        try
        {
            var result = await next(context);

            if (TryExtractCacheableSuccess(result, out var statusCode, out var responseJson))
            {
                record.StatusCode = statusCode;
                record.ResponseJson = responseJson;
                record.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                // Do not cache non-2xx deterministic errors as completed creations.
                db.IdempotencyKeys.Remove(record);
                await db.SaveChangesAsync(ct);
            }

            return result;
        }
        catch
        {
            db.IdempotencyKeys.Remove(record);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public static bool IsValidIdempotencyKey(string raw, out string key)
    {
        key = raw.Trim();
        if (key.Length is < 1 or > 128)
        {
            return false;
        }

        foreach (var ch in key)
        {
            if (ch < 0x21 || ch > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveRouteTemplate(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint routeEndpoint &&
            !string.IsNullOrWhiteSpace(routeEndpoint.RoutePattern.RawText))
        {
            return routeEndpoint.RoutePattern.RawText!;
        }

        return context.Request.Path.Value ?? "/";
    }

    private static async Task<string> ComputeRequestBodyHashAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Body.CanSeek)
        {
            request.EnableBuffering();
        }

        request.Body.Position = 0;
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        request.Body.Position = 0;

        var rawBytes = ms.ToArray();
        if (rawBytes.Length == 0 && (request.ContentLength ?? 0) > 0)
        {
            throw new InvalidOperationException(
                "Request body was already consumed before buffering was enabled for Idempotency-Key hashing.");
        }

        if (rawBytes.Length == 0)
        {
            return Convert.ToHexStringLower(SHA256.HashData(rawBytes));
        }

        // Canonicalize JSON whitespace/property spacing when valid JSON so equivalent payloads hash identically.
        try
        {
            using var doc = JsonDocument.Parse(rawBytes);
            var canonical = JsonSerializer.SerializeToUtf8Bytes(doc.RootElement);
            return Convert.ToHexStringLower(SHA256.HashData(canonical));
        }
        catch (JsonException)
        {
            return Convert.ToHexStringLower(SHA256.HashData(rawBytes));
        }
    }

    private static bool TryExtractCacheableSuccess(object? result, out int statusCode, out string responseJson)
    {
        statusCode = StatusCodes.Status200OK;
        responseJson = "{}";

        if (result is null)
        {
            return false;
        }

        if (result is IStatusCodeHttpResult statusResult)
        {
            if (statusResult.StatusCode is not (>= 200 and < 300))
            {
                return false;
            }

            statusCode = statusResult.StatusCode.Value;
            if (result is IValueHttpResult valueResult && valueResult.Value is not null)
            {
                responseJson = JsonSerializer.Serialize(valueResult.Value, JsonOptions);
            }
            return true;
        }

        if (result is IResult)
        {
            return false;
        }

        responseJson = JsonSerializer.Serialize(result, JsonOptions);
        return true;
    }
}
