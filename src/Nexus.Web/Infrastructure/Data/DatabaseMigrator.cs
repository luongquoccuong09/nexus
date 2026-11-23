using Microsoft.EntityFrameworkCore;

namespace Nexus.Web.Infrastructure.Data;

/// <summary>
/// Runs EF Core migrations exclusively using the dedicated migration role connection string (M0.2).
/// Never runs DDL under the runtime application role.
/// </summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(
        string migrationConnectionString,
        bool requireTls = true,
        CancellationToken cancellationToken = default)
    {
        DatabaseOptions.ValidateConnectionString(migrationConnectionString, requireTls, "Migration");

        var optionsBuilder = new DbContextOptionsBuilder<NexusDbContext>()
            .UseNpgsql(migrationConnectionString);

        await using var db = new NexusDbContext(optionsBuilder.Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
