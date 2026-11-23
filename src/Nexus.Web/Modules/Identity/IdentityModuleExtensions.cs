using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Web.Infrastructure.Data;

namespace Nexus.Web.Modules.Identity;

public static class IdentityModuleExtensions
{
    public const string RegisterRateLimitPolicy = "auth-register";
    public const string LoginRateLimitPolicy = "auth-login";

    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(IdentityModuleOptions.SectionName);
        var options = section.Get<IdentityModuleOptions>() ?? new IdentityModuleOptions();

        services.Configure<IdentityModuleOptions>(section);

        services.AddIdentityCore<NexusUser>(identity =>
            {
                identity.Password.RequiredLength = CommonPasswordValidator.MinPasswordLength;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;

                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = options.MaxFailedLoginAttempts;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(options.LockoutMinutes);

                identity.User.RequireUniqueEmail = false;
                identity.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789._-";
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<NexusDbContext>()
            .AddPasswordValidator<CommonPasswordValidator>()
            .AddDefaultTokenProviders();

        services.TryAddSingleton<IRegistrationCaptchaVerifier, SwitchableRegistrationCaptchaVerifier>();

        services.Configure<RateLimiterOptions>(rate =>
        {
            rate.AddPolicy(RegisterRateLimitPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "local";
                return RateLimitPartition.GetFixedWindowLimiter($"reg:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RegisterRateLimitPerMinutePerIp,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });

            rate.AddPolicy(LoginRateLimitPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "local";
                return RateLimitPartition.GetFixedWindowLimiter($"login:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.LoginRateLimitPerMinutePerIp,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        return services;
    }
}
