using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Identity;

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

if (args.Contains("--bootstrap-admin", StringComparer.OrdinalIgnoreCase))
{
    var bootstrapApp = NexusHost.Build(args);
    var cfg = bootstrapApp.Configuration;
    var username = cfg["BootstrapAdmin:Username"] ?? throw new InvalidOperationException("BootstrapAdmin:Username is required.");
    var password = cfg["BootstrapAdmin:Password"] ?? throw new InvalidOperationException("BootstrapAdmin:Password is required.");
    var displayName = cfg["BootstrapAdmin:DisplayName"] ?? "System Administrator";
    await IdentityBootstrapService.BootstrapSystemAdminAsync(bootstrapApp.Services, username, password, displayName);
    return;
}

if (args.Contains("--issue-account-reset", StringComparer.OrdinalIgnoreCase))
{
    await using var recoveryApp = NexusHost.Build(args);
    var username = recoveryApp.Configuration["AccountRecovery:Username"]
        ?? throw new InvalidOperationException("AccountRecovery:Username is required.");
    Console.WriteLine(await IdentityBootstrapService.IssueAccountRecoveryAsync(recoveryApp.Services, username));
    return;
}

var app = NexusHost.Build(args);
app.Run();
