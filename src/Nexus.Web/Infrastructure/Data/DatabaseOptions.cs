using Npgsql;

namespace Nexus.Web.Infrastructure.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "NexusDatabase";
    public const int ForbiddenSupabaseTransactionPoolerPort = 6543;

    public string? RuntimeConnectionString { get; set; }
    public string? MigrationConnectionString { get; set; }
    public bool RequireTls { get; set; } = true;

    public static DatabaseOptions BindFrom(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (string.IsNullOrWhiteSpace(options.RuntimeConnectionString))
        {
            options.RuntimeConnectionString = configuration.GetConnectionString("Runtime");
        }

        if (string.IsNullOrWhiteSpace(options.MigrationConnectionString))
        {
            options.MigrationConnectionString = configuration.GetConnectionString("Migration");
        }

        return options;
    }

    public static void ValidateConnectionString(string connectionString, bool requireTls, string roleLabel)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"PostgreSQL {roleLabel} connection string must not be empty.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (builder.Port == ForbiddenSupabaseTransactionPoolerPort)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {roleLabel} connection string uses port {ForbiddenSupabaseTransactionPoolerPort} (Supabase Transaction Mode). " +
                "Use Session Mode on port 5432 instead.");
        }

        if (requireTls && builder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {roleLabel} connection string must enforce TLS (SslMode Require, VerifyCA, or VerifyFull) when RequireTls is enabled.");
        }
    }
}
