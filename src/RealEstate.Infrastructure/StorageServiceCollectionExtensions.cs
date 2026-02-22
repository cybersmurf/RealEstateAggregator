using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Infrastructure;

/// <summary>
/// Extension methods for registering storage services.
/// </summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Register IStorageService based on configuration.
    /// Provider can be: "Local", "GoogleDrive", "OneDrive"
    /// </summary>
    public static IServiceCollection AddStorageService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"]?.ToLowerInvariant() ?? "local";

        switch (provider)
        {
            case "googledrive":
                // TODO: Implement GoogleDriveStorageService
                throw new NotImplementedException("Google Drive storage not yet implemented. Use 'Local' for now.");

            case "onedrive":
                // TODO: Implement OneDriveStorageService
                throw new NotImplementedException("OneDrive storage not yet implemented. Use 'Local' for now.");

            case "local":
            default:
                services.AddSingleton<IStorageService, LocalStorageService>();
                break;
        }

        return services;
    }
}
