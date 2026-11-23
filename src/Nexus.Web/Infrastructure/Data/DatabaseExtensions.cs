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

        services.AddHttpContextAccessor();

        services.AddDbContext<NexusDbContext>(options =>
            options.UseNpgsql(dbOptions.RuntimeConnectionString));

        // Replace default in-memory status lookup with PostgreSQL-backed lookup when DB is configured.
        services.AddSingleton<IAccountStatusLookup, DatabaseAccountStatusLookup>();

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

    private sealed class DatabaseAccountStatusLookup(IServiceScopeFactory scopeFactory) : IAccountStatusLookup
    {
        public async Task<AccountStatus?> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            var state = await GetRuntimeStateAsync(userId, cancellationToken);
            return state?.Status;
        }

        public async Task<AccountRuntimeState?> GetRuntimeStateAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var row = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Status, u.SecurityStamp })
                .SingleOrDefaultAsync(cancellationToken);

            return row is null ? null : new AccountRuntimeState(row.Status, row.SecurityStamp);
        }
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
