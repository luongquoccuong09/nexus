using System.Collections.Concurrent;
using System.Security.Claims;

namespace Nexus.Web.Infrastructure.Security;

public sealed record AccountRuntimeState(AccountStatus Status, string? SecurityStamp);

public interface IAccountStatusLookup
{
    Task<AccountStatus?> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    async Task<AccountRuntimeState?> GetRuntimeStateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(userId, cancellationToken);
        return status.HasValue ? new AccountRuntimeState(status.Value, null) : null;
    }
}

public sealed class InMemoryAccountStatusLookup : IAccountStatusLookup
{
    private readonly ConcurrentDictionary<Guid, AccountRuntimeState> _states = new();

    public void SetStatus(Guid userId, AccountStatus status, string? securityStamp = null) =>
        _states[userId] = new AccountRuntimeState(status, securityStamp);

    public Task<AccountStatus?> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_states.TryGetValue(userId, out var state) ? (AccountStatus?)state.Status : null);
    }

    public Task<AccountRuntimeState?> GetRuntimeStateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_states.TryGetValue(userId, out var state) ? state : null);
    }
}

public static class ClaimsPrincipalExtensions
{
    public const string SecurityStampClaimType = "nexus.security_stamp";
    public const string SessionIdClaimType = "nexus.session_id";
    public const string MfaVerifiedClaimType = "nexus.mfa_verified";

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

    public static string? GetSecurityStamp(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(SecurityStampClaimType);

    public static string? GetSessionId(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(SessionIdClaimType);
}
