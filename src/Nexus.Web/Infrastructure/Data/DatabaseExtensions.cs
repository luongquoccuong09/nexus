using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Infrastructure.Data;

public static class DatabaseExtensions
{
    public static IServiceCollection AddNexusDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var dbOptions = DatabaseOptions.BindFrom(configuration);

        if (string.IsNullOrWhiteSpace(dbOptions.RuntimeConnectionString))
        {
            return services;
        }

        DatabaseOptions.ValidateConnectionString(
            dbOptions.RuntimeConnectionString,
            dbOptions.RequireTls,
            "Runtime");

        services.AddDbContext<NexusDbContext>(options =>
            options.UseNpgsql(dbOptions.RuntimeConnectionString));

        // M0.8: persist ASP.NET Core Data Protection keys and BFF sessions in PostgreSQL.
        services.AddDataProtection()
            .SetApplicationName("Nexus.Web")
            .PersistKeysToDbContext<NexusDbContext>();

        services.AddSingleton<PostgreSqlSessionTicketStore>();
        services.AddOptions<CookieAuthenticationOptions>(SecurityDefaultsExtensions.SessionAuthScheme)
            .Configure<PostgreSqlSessionTicketStore>((cookieOptions, store) =>
            {
                cookieOptions.SessionStore = store;
            });

        services.AddHealthChecks()
            .AddCheck<PostgreSqlReadinessHealthCheck>("postgres", tags: ["ready"]);

        return services;
    }

    private sealed class PostgreSqlReadinessHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                var canConnect = await db.Database.CanConnectAsync(cancellationToken);
                return canConnect
                    ? HealthCheckResult.Healthy("PostgreSQL connection succeeded.")
                    : HealthCheckResult.Unhealthy("PostgreSQL connection failed.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", ex);
            }
        }
    }
}
