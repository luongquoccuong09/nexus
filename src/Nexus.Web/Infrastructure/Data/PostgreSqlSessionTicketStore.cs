using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Meetings;

namespace Nexus.Web.Infrastructure.Data;

/// <summary>
/// Persists ASP.NET Core BFF authentication tickets in PostgreSQL (M0.8 / M1.7)
/// so cookies carry only an opaque session ID and sessions can be listed and revoked server-side.
/// </summary>
public sealed class PostgreSqlSessionTicketStore : ITicketStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PostgreSqlSessionTicketStore(
        IServiceScopeFactory scopeFactory,
        IHttpContextAccessor httpContextAccessor)
    {
        _scopeFactory = scopeFactory;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;

        // Stamp the session ID into the stored ticket so handlers can identify the current device.
        var enrichedTicket = WithSessionIdClaim(ticket, key);
        var userId = enrichedTicket.Principal.TryGetUserId(out var parsedUserId) ? parsedUserId : (Guid?)null;
        var securityStamp = enrichedTicket.Principal.GetSecurityStamp();

        var httpContext = _httpContextAccessor.HttpContext;
        var userAgent = Truncate(httpContext?.Request.Headers.UserAgent.ToString(), 512);
        var ipAddress = Truncate(httpContext?.Connection.RemoteIpAddress?.ToString(), 64);

        var entity = new AppSessionEntity
        {
            Id = key,
            UserId = userId,
            TicketBytes = TicketSerializer.Default.Serialize(enrichedTicket),
            SecurityStamp = securityStamp,
            UserAgent = userAgent,
            IpAddress = ipAddress,
            CreatedAt = now,
            LastActivityAt = now,
            ExpiresAt = enrichedTicket.Properties.ExpiresUtc
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        db.AppSessions.Add(entity);
        await db.SaveChangesAsync();
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var now = DateTimeOffset.UtcNow;
        var enrichedTicket = WithSessionIdClaim(ticket, key);
        var bytes = TicketSerializer.Default.Serialize(enrichedTicket);
        var expiresAt = enrichedTicket.Properties.ExpiresUtc;
        var securityStamp = enrichedTicket.Principal.GetSecurityStamp();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        await db.AppSessions
            .Where(s => s.Id == key && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.TicketBytes, bytes)
                .SetProperty(s => s.SecurityStamp, securityStamp)
                .SetProperty(s => s.LastActivityAt, now)
                .SetProperty(s => s.ExpiresAt, expiresAt));
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        var entity = await db.AppSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(s =>
                s.Id == key &&
                s.RevokedAt == null &&
                (s.ExpiresAt == null || s.ExpiresAt > now));

        return entity is null ? null : TicketSerializer.Default.Deserialize(entity.TicketBytes);
    }

    public async Task RemoveAsync(string key)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        var revoked = await db.AppSessions
            .Where(s => s.Id == key && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now));
        if (revoked != 0)
            await scope.ServiceProvider.GetRequiredService<MeetingRevocations>().RecheckSessionAsync(key, CancellationToken.None);
    }

    private static AuthenticationTicket WithSessionIdClaim(AuthenticationTicket ticket, string sessionId)
    {
        var clone = ticket.Principal.Clone();
        if (clone.Identity is ClaimsIdentity identity)
        {
            var existing = identity.FindFirst(ClaimsPrincipalExtensions.SessionIdClaimType);
            if (existing is not null)
            {
                identity.RemoveClaim(existing);
            }
            identity.AddClaim(new Claim(ClaimsPrincipalExtensions.SessionIdClaimType, sessionId));
        }

        return new AuthenticationTicket(clone, ticket.Properties, ticket.AuthenticationScheme);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
