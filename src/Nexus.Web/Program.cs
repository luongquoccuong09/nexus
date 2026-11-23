using Nexus.Web;
using Nexus.Web.Infrastructure.Data;

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    var dbOptions = DatabaseOptions.BindFrom(config);
    if (string.IsNullOrWhiteSpace(dbOptions.MigrationConnectionString))
    {
        throw new InvalidOperationException("ConnectionStrings:Migration (or NexusDatabase:MigrationConnectionString) is required for --migrate.");
    }

    await DatabaseMigrator.MigrateAsync(dbOptions.MigrationConnectionString, dbOptions.RequireTls);
    return;
}

var app = NexusHost.Build(args);
app.Run();
