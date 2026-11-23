using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Infrastructure.Data;

/// <summary>
/// Persists ASP.NET Core BFF authentication tickets in PostgreSQL (M0.8)
/// so cookies carry only an opaque session ID and sessions can be revoked server-side.
/// </summary>
public sealed class PostgreSqlSessionTicketStore : ITicketStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PostgreSqlSessionTicketStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var userId = ticket.Principal.TryGetUserId(out var parsedUserId) ? parsedUserId : (Guid?)null;

        var entity = new AppSessionEntity
        {
            Id = key,
            UserId = userId,
            TicketBytes = TicketSerializer.Default.Serialize(ticket),
            CreatedAt = now,
            LastActivityAt = now,
            ExpiresAt = ticket.Properties.ExpiresUtc
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
        var bytes = TicketSerializer.Default.Serialize(ticket);
        var expiresAt = ticket.Properties.ExpiresUtc;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        await db.AppSessions
            .Where(s => s.Id == key && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.TicketBytes, bytes)
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

        await db.AppSessions
            .Where(s => s.Id == key && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now));
    }
}
