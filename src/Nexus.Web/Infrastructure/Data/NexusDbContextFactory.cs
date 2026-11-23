using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nexus.Web.Infrastructure.Data;

/// <summary>
/// Design-time factory for `dotnet ef migrations` CLI commands (M0.2).
/// </summary>
public sealed class NexusDbContextFactory : IDesignTimeDbContextFactory<NexusDbContext>
{
    public NexusDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("NEXUS_MIGRATION_CONNECTION")
                 ?? "Host=127.0.0.1;Port=5432;Database=nexus_design;Username=nexus_migrator";

        var optionsBuilder = new DbContextOptionsBuilder<NexusDbContext>()
            .UseNpgsql(cs);

        return new NexusDbContext(optionsBuilder.Options);
    }
}
