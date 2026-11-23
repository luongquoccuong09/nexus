using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nexus.Web.Infrastructure.Storage;

public static class StorageExtensions
{
    public static IServiceCollection AddNexusStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(FileStorageOptions.SectionName);
        var options = section.Get<FileStorageOptions>() ?? new FileStorageOptions();
        options.Validate();

        services.AddOptions<FileStorageOptions>()
            .Bind(section)
            .Validate(o =>
            {
                o.Validate();
                return true;
            })
            .ValidateOnStart();

        if (string.Equals(options.Provider, "Supabase", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<SupabaseFileStorage>();
            services.TryAddTransient<IFileStorage>(sp => sp.GetRequiredService<SupabaseFileStorage>());
        }
        else
        {
            services.TryAddSingleton<IFileStorage, LocalFileStorage>();
        }

        services.TryAddSingleton<IFileScanner, PrototypeFileScannerStub>();
        return services;
    }
}
