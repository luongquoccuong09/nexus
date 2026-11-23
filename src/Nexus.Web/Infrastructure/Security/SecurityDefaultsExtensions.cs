using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nexus.Web.Infrastructure.Security;

public static class SecurityDefaultsExtensions
{
    public const string SessionAuthScheme = "Nexus.Session";
    public const string CorsPolicyName = "NexusStrictCors";

    public static IServiceCollection AddNexusSecurityDefaults(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(NexusSecurityOptions.SectionName);
        var options = section.Get<NexusSecurityOptions>() ?? new NexusSecurityOptions();
        options.Validate();

        services.AddOptions<NexusSecurityOptions>()
            .Bind(section)
            .Validate(o =>
            {
                o.Validate();
                return true;
            })
            .ValidateOnStart();

        services.Configure<KestrelServerOptions>(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
        });

        services.AddAuthentication(SessionAuthScheme)
            .AddCookie(SessionAuthScheme, cookie =>
            {
                cookie.Cookie.Name = "__Host-nexus-session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(auth =>
        {
            auth.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddCors(cors =>
        {
            cors.AddPolicy(CorsPolicyName, policy =>
            {
                if (options.AllowedOrigins.Length > 0)
                {
                    policy.WithOrigins(options.AllowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                }
            });
        });

        services.AddAntiforgery(anti =>
        {
            anti.HeaderName = "X-CSRF-TOKEN";
            anti.Cookie.HttpOnly = true;
            anti.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddRateLimiter(rate =>
        {
            rate.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rate.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (httpContext.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                    httpContext.Request.Path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("health");
                }

                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "local";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        services.TryAddSingleton<IAccountStatusLookup, InMemoryAccountStatusLookup>();
        services.TryAddSingleton<IUserConnectionRegistry, InMemoryUserConnectionRegistry>();
        services.TryAddSingleton<IAuthorizationService, NexusAuthorizationService>();
        services.AddSingleton<ActiveUserHubFilter>();

        services.AddSignalR(hub =>
        {
            hub.AddFilter<ActiveUserHubFilter>();
        });

        return services;
    }

    public static IApplicationBuilder UseNexusSecurityDefaults(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(Nexus.Web.Infrastructure.Idempotency.IdempotencyEndpointFilter.HeaderName))
            {
                context.Request.EnableBuffering();
            }

            await next();
        });
        app.UseMiddleware<WebSocketOriginMiddleware>();
        app.UseCors(CorsPolicyName);
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<ActiveUserMiddleware>();
        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }
}
