using Microsoft.AspNetCore.SignalR;

namespace Nexus.Web.Infrastructure.Security;

public sealed class ActiveUserHubFilter : IHubFilter
{
    private readonly IAccountStatusLookup _statusLookup;
    private readonly IUserConnectionRegistry _connectionRegistry;

    public ActiveUserHubFilter(
        IAccountStatusLookup statusLookup,
        IUserConnectionRegistry connectionRegistry)
    {
        _statusLookup = statusLookup;
        _connectionRegistry = connectionRegistry;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var userId = await EnsureActiveAsync(context.Context);
        _connectionRegistry.Register(userId, context.Context);
        await next(context);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (context.Context.User.TryGetUserId(out var userId))
        {
            _connectionRegistry.Unregister(userId, context.Context.ConnectionId);
        }

        return next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await EnsureActiveAsync(invocationContext.Context);
        return await next(invocationContext);
    }

    private async Task<Guid> EnsureActiveAsync(HubCallerContext callerContext)
    {
        if (!callerContext.User.TryGetUserId(out var userId))
        {
            callerContext.Abort();
            throw new HubException("Account is not active.");
        }

        var runtime = await _statusLookup.GetRuntimeStateAsync(userId, callerContext.ConnectionAborted);
        if (runtime is null || runtime.Status != AccountStatus.Active)
        {
            callerContext.Abort();
            throw new HubException("Account is not active.");
        }

        var claimStamp = callerContext.User.GetSecurityStamp();
        if (!string.IsNullOrEmpty(claimStamp) &&
            !string.IsNullOrEmpty(runtime.SecurityStamp) &&
            !string.Equals(claimStamp, runtime.SecurityStamp, StringComparison.Ordinal))
        {
            callerContext.Abort();
            throw new HubException("Session has been revoked.");
        }

        return userId;
    }
}
