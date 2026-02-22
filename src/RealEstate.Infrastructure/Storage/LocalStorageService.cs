using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RealEstate.Infrastructure.Storage;

/// <summary>
/// Local filesystem storage implementation.
/// Stores files in wwwroot/uploads/{folder}/{uniqueFileName}
/// </summary>
public sealed class LocalStorageService(
    IConfiguration configuration,
    ILogger<LocalStorageService> logger) : IStorageService
{
    private readonly string _basePath = configuration["Storage:Local:BasePath"] ?? "wwwroot/uploads";

    public async Task<string> UploadFileAsync(
        Stream stream,
        string fileName,
        string folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        // Sanitize folder path for filesystem
        var sanitizedFolder = folder.Replace("/", Path.DirectorySeparatorChar.ToString());
        var targetDir = Path.Combine(_basePath, sanitizedFolder);

        // Create directory if not exists
        Directory.CreateDirectory(targetDir);

        // Generate unique filename to avoid collisions
        var extension = Path.GetExtension(fileName);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var uniqueFileName = $"{fileNameWithoutExt}_{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(targetDir, uniqueFileName);

        // Write stream to file
        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(fileStream, cancellationToken);

        logger.LogInformation(
            "Uploaded file {FileName} to local storage: {FilePath}",
            fileName,
            filePath);

        // Return relative path from wwwroot (for serving via static files)
        var relativePath = Path.Combine("uploads", sanitizedFolder, uniqueFileName)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relativePath;
    }

    public Task<string?> GetFileUrlAsync(
        string storedUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedUrl);

        // Local files served via static files middleware
        // Return path with leading slash for absolute URL
        var publicUrl = storedUrl.StartsWith('/') ? storedUrl : $"/{storedUrl}";

        return Task.FromResult<string?>(publicUrl);
    }

    public Task DeleteFileAsync(
        string storedUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedUrl);

        var fullPath = Path.Combine(_basePath, storedUrl.TrimStart('/'));

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            logger.LogInformation("Deleted file from local storage: {FilePath}", fullPath);
        }
        else
        {
            logger.LogWarning("File not found for deletion: {FilePath}", fullPath);
        }

        return Task.CompletedTask;
    }
}
