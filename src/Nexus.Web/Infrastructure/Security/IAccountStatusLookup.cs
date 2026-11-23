using System.Collections.Concurrent;
using System.Security.Claims;

namespace Nexus.Web.Infrastructure.Security;

public interface IAccountStatusLookup
{
    Task<AccountStatus?> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAccountStatusLookup : IAccountStatusLookup
{
    private readonly ConcurrentDictionary<Guid, AccountStatus> _statuses = new();

    public void SetStatus(Guid userId, AccountStatus status) => _statuses[userId] = status;

    public Task<AccountStatus?> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_statuses.TryGetValue(userId, out var status) ? (AccountStatus?)status : null);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static bool TryGetUserId(this ClaimsPrincipal? principal, out Guid userId)
    {
        userId = Guid.Empty;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");

        return Guid.TryParse(raw, out userId);
    }
}
