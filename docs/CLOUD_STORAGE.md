# Cloud Storage & User Photo Upload – Technical Design

**Project:** RealEstateAggregator  
**Version:** 1.0  
**Date:** 22. února 2026  
**Status:** Design Approved

---

## Overview

Tento dokument specifikuje implementaci cloud storage pro:
1. **User photo uploads** – fotky z prohlídek uploadované z Macu/iPhone
2. **Google Drive integration** – primární storage provider
3. **OneDrive integration** – alternativní provider
4. **Local storage** – fallback pro development

---

## Table of Contents

- [Architecture](#architecture)
- [OAuth 2.0 Authentication](#oauth-20-authentication)
- [Storage Service Interface](#storage-service-interface)
- [Database Schema](#database-schema)
- [API Endpoints](#api-endpoints)
- [Blazor UI Components](#blazor-ui-components)
- [HEIC Support](#heic-support)
- [Security Considerations](#security-considerations)
- [Implementation Priority](#implementation-priority)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Blazor UI (Mac/iPhone)                   │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  ListingDetail.razor                                      │  │
│  │  - Status = "Visited" → Show Upload Button                │  │
│  │  - MudFileUpload (multiple, .jpg/.png/.heic)              │  │
│  │  - MudCarousel (user photos gallery)                      │  │
│  └───────────────────────────────────────────────────────────┘  │
│                            ↓ HTTP POST                          │
└─────────────────────────────────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│                    .NET API (:5001)                             │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  POST /api/listings/{id}/my-photos                        │  │
│  │  GET  /api/listings/{id}/my-photos                        │  │
│  │  DELETE /api/listings/{id}/my-photos/{photoId}            │  │
│  └───────────────────────────────────────────────────────────┘  │
│                            ↓                                    │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  IStorageService                                          │  │
│  │  - UploadFileAsync()                                      │  │
│  │  - GetFileUrlAsync()                                      │  │
│  │  - DeleteFileAsync()                                      │  │
│  └───────────────────────────────────────────────────────────┘  │
│         ↓                    ↓                    ↓             │
│  GoogleDrive         OneDrive            LocalStorage            │
│  (OAuth2)            (MSAL)              (wwwroot/uploads)       │
└─────────────────────────────────────────────────────────────────┘
                             ↓
┌─────────────────────────────────────────────────────────────────┐
│                    PostgreSQL (:5432)                           │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  user_listing_photos                                      │  │
│  │  - id, listing_id, stored_url, original_filename          │  │
│  │  - file_size_bytes, taken_at, uploaded_at, notes          │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

---

## OAuth 2.0 Authentication

### Problem Statement

Both Google Drive and OneDrive use **OAuth 2.0**. For server-to-server applications without user UI, the most practical flow is:

- **Google:** Service Account (for workspace admin) or OAuth Authorization Code Flow
- **Microsoft:** Client Credentials Flow (limited) or Authorization Code Flow

**Recommended:** **OAuth Authorization Code Flow** with one-time manual authorization and stored refresh token.

### Flow Diagram

```
1. Admin runs: dotnet run auth google
   → Opens browser to Google consent screen
   → User approves access
   → Callback URL receives authorization code
   → Exchange for access_token + refresh_token
   → Store refresh_token in secrets.json / env variable

2. Application startup:
   → Read refresh_token from config
   → Exchange for fresh access_token (valid 1 hour)
   → Use access_token for API calls

3. When access_token expires:
   → Use refresh_token to get new access_token
   → No user interaction needed
```

### Configuration

**appsettings.json** (DO NOT commit secrets!)
```json
{
  "Storage": {
    "Provider": "GoogleDrive",  // or "OneDrive" or "Local"
    "GoogleDrive": {
      "ClientId": "xxx.apps.googleusercontent.com",
      "ClientSecret": "GOCSPX-xxx",
      "RefreshToken": "",  // Empty in appsettings, load from secrets
      "FolderName": "RealEstateAggregator/UserPhotos"
    },
    "OneDrive": {
      "ClientId": "xxx",
      "ClientSecret": "xxx",
      "TenantId": "common",
      "RefreshToken": "",
      "FolderPath": "RealEstateAggregator/UserPhotos"
    },
    "Local": {
      "BasePath": "wwwroot/uploads"
    }
  }
}
```

**User Secrets** (development)
```bash
dotnet user-secrets set "Storage:GoogleDrive:RefreshToken" "1//xxx"
dotnet user-secrets set "Storage:OneDrive:RefreshToken" "M.xxx"
```

**Environment Variables** (production)
```bash
export Storage__GoogleDrive__RefreshToken="1//xxx"
export Storage__OneDrive__RefreshToken="M.xxx"
```

### One-Time Setup

**CLI Command:**
```bash
# Add to Program.cs for dev/staging only
dotnet run -- auth google
dotnet run -- auth onedrive
```

**Or Web Endpoint** (protected, admin only):
```csharp
// GET /api/auth/google/authorize
// → Redirects to Google OAuth consent screen

// GET /api/auth/google/callback?code=xxx
// → Exchanges code for tokens, stores refresh_token, redirects to success page
```

---

## Storage Service Interface

### IStorageService

```csharp
namespace RealEstate.Infrastructure.Storage;

public interface IStorageService
{
    /// <summary>
    /// Upload file to configured storage provider.
    /// </summary>
    /// <param name="stream">File content stream</param>
    /// <param name="fileName">Original filename (e.g., "IMG_1234.jpg")</param>
    /// <param name="folder">Logical folder path (e.g., "listings/{listingId}/my_photos")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stored URL (Drive file ID or local path)</returns>
    Task<string> UploadFileAsync(
        Stream stream, 
        string fileName, 
        string folder, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get public/shareable URL for file.
    /// </summary>
    /// <param name="storedUrl">URL returned from UploadFileAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Public URL (or null if not available)</returns>
    Task<string?> GetFileUrlAsync(
        string storedUrl, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete file from storage.
    /// </summary>
    /// <param name="storedUrl">URL returned from UploadFileAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteFileAsync(
        string storedUrl, 
        CancellationToken cancellationToken = default);
}
```

### Implementation Classes

#### LocalStorageService

```csharp
public class LocalStorageService : IStorageService
{
    private readonly string _basePath;
    
    public LocalStorageService(IConfiguration config)
    {
        _basePath = config["Storage:Local:BasePath"] ?? "wwwroot/uploads";
    }
    
    public async Task<string> UploadFileAsync(
        Stream stream, 
        string fileName, 
        string folder, 
        CancellationToken ct)
    {
        var sanitizedFolder = folder.Replace("/", Path.DirectorySeparatorChar.ToString());
        var targetDir = Path.Combine(_basePath, sanitizedFolder);
        Directory.CreateDirectory(targetDir);
        
        var uniqueFileName = $"{Guid.NewGuid()}_{fileName}";
        var filePath = Path.Combine(targetDir, uniqueFileName);
        
        using var fileStream = new FileStream(filePath, FileMode.Create);
        await stream.CopyToAsync(fileStream, ct);
        
        // Return relative path from wwwroot
        return Path.Combine("uploads", sanitizedFolder, uniqueFileName)
            .Replace(Path.DirectorySeparatorChar, '/');
    }
    
    public Task<string?> GetFileUrlAsync(string storedUrl, CancellationToken ct)
    {
        // Local files served via static files middleware
        return Task.FromResult<string?>($"/{storedUrl}");
    }
    
    public Task DeleteFileAsync(string storedUrl, CancellationToken ct)
    {
        var fullPath = Path.Combine(_basePath, storedUrl.TrimStart('/'));
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        
        return Task.CompletedTask;
    }
}
```

#### GoogleDriveStorageService

```csharp
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

public class GoogleDriveStorageService : IStorageService
{
    private readonly DriveService _driveService;
    private readonly string _folderName;
    
    public GoogleDriveStorageService(IConfiguration config, ILogger<GoogleDriveStorageService> logger)
    {
        var clientId = config["Storage:GoogleDrive:ClientId"];
        var clientSecret = config["Storage:GoogleDrive:ClientSecret"];
        var refreshToken = config["Storage:GoogleDrive:RefreshToken"];
        _folderName = config["Storage:GoogleDrive:FolderName"] ?? "RealEstateAggregator";
        
        if (string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException("Google Drive refresh token not configured");
        
        var credential = new UserCredential(
            new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = clientId,
                        ClientSecret = clientSecret
                    }
                }),
            "user",
            new Google.Apis.Auth.OAuth2.Responses.TokenResponse
            {
                RefreshToken = refreshToken
            });
        
        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "RealEstateAggregator"
        });
    }
    
    public async Task<string> UploadFileAsync(
        Stream stream, 
        string fileName, 
        string folder, 
        CancellationToken ct)
    {
        // Get or create folder
        var folderId = await GetOrCreateFolderAsync($"{_folderName}/{folder}", ct);
        
        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName,
            Parents = new List<string> { folderId }
        };
        
        var request = _driveService.Files.Create(
            fileMetadata,
            stream,
            MimeTypeMap.GetMimeType(fileName));
        
        request.Fields = "id";
        var file = await request.UploadAsync(ct);
        
        if (file.Status != Google.Apis.Upload.UploadStatus.Completed)
            throw new Exception($"Upload failed: {file.Exception?.Message}");
        
        return request.ResponseBody.Id;  // Return Drive file ID
    }
    
    public async Task<string?> GetFileUrlAsync(string fileId, CancellationToken ct)
    {
        // Make file publicly readable (or use permission management)
        var permission = new Google.Apis.Drive.v3.Data.Permission
        {
            Type = "anyone",
            Role = "reader"
        };
        
        await _driveService.Permissions.Create(permission, fileId).ExecuteAsync(ct);
        
        return $"https://drive.google.com/uc?id={fileId}&export=download";
    }
    
    public async Task DeleteFileAsync(string fileId, CancellationToken ct)
    {
        await _driveService.Files.Delete(fileId).ExecuteAsync(ct);
    }
    
    private async Task<string> GetOrCreateFolderAsync(string folderPath, CancellationToken ct)
    {
        // Implementation: Split path, recursively create folders
        // Return final folder ID
        // (simplified for brevity)
        throw new NotImplementedException();
    }
}
```

#### OneDriveStorageService

```csharp
using Microsoft.Graph;
using Microsoft.Identity.Client;

public class OneDriveStorageService : IStorageService
{
    private readonly GraphServiceClient _graphClient;
    private readonly string _folderPath;
    
    public OneDriveStorageService(IConfiguration config, ILogger<OneDriveStorageService> logger)
    {
        var clientId = config["Storage:OneDrive:ClientId"];
        var clientSecret = config["Storage:OneDrive:ClientSecret"];
        var tenantId = config["Storage:OneDrive:TenantId"] ?? "common";
        var refreshToken = config["Storage:OneDrive:RefreshToken"];
        _folderPath = config["Storage:OneDrive:FolderPath"] ?? "RealEstateAggregator";
        
        if (string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException("OneDrive refresh token not configured");
        
        var confidentialClient = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}"))
            .Build();
        
        // Use refresh token to get access token
        var authProvider = new DelegateAuthenticationProvider(async (request) =>
        {
            var result = await confidentialClient
                .AcquireTokenByRefreshToken(new[] { "https://graph.microsoft.com/.default" }, refreshToken)
                .ExecuteAsync();
            
            request.Headers.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
        });
        
        _graphClient = new GraphServiceClient(authProvider);
    }
    
    public async Task<string> UploadFileAsync(
        Stream stream, 
        string fileName, 
        string folder, 
        CancellationToken ct)
    {
        var fullPath = $"/{_folderPath}/{folder}/{fileName}";
        
        var uploadedFile = await _graphClient.Me.Drive.Root
            .ItemWithPath(fullPath)
            .Content
            .Request()
            .PutAsync<DriveItem>(stream, ct);
        
        return uploadedFile.Id;  // Return OneDrive item ID
    }
    
    public async Task<string?> GetFileUrlAsync(string itemId, CancellationToken ct)
    {
        var item = await _graphClient.Me.Drive.Items[itemId]
            .Request()
            .GetAsync(ct);
        
        return item.WebUrl;  // Public sharing URL
    }
    
    public async Task DeleteFileAsync(string itemId, CancellationToken ct)
    {
        await _graphClient.Me.Drive.Items[itemId]
            .Request()
            .DeleteAsync(ct);
    }
}
```

### Service Registration

```csharp
// src/RealEstate.Infrastructure/StorageServiceCollectionExtensions.cs

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStorageService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"];
        
        switch (provider?.ToLowerInvariant())
        {
            case "googledrive":
                services.AddSingleton<IStorageService, GoogleDriveStorageService>();
                break;
            case "onedrive":
                services.AddSingleton<IStorageService, OneDriveStorageService>();
                break;
            case "local":
            default:
                services.AddSingleton<IStorageService, LocalStorageService>();
                break;
        }
        
        return services;
    }
}
```

**Program.cs:**
```csharp
builder.Services.AddStorageService(builder.Configuration);
```

---

## Database Schema

### UserListingPhoto Entity

```csharp
// src/RealEstate.Domain/Entities/UserListingPhoto.cs

namespace RealEstate.Domain.Entities;

public class UserListingPhoto
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// Reference to listing
    /// </summary>
    public Guid ListingId { get; set; }
    
    /// <summary>
    /// Storage provider URL/ID
    /// - Google Drive: file ID (e.g., "1a2b3c4d5e")
    /// - OneDrive: item ID
    /// - Local: relative path (e.g., "uploads/listings/xxx/photo.jpg")
    /// </summary>
    public string StoredUrl { get; set; } = null!;
    
    /// <summary>
    /// Original filename uploaded by user
    /// </summary>
    public string OriginalFileName { get; set; } = null!;
    
    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSizeBytes { get; set; }
    
    /// <summary>
    /// Photo taken timestamp (from EXIF if available, otherwise uploaded_at)
    /// </summary>
    public DateTime TakenAt { get; set; }
    
    /// <summary>
    /// Upload timestamp
    /// </summary>
    public DateTime UploadedAt { get; set; }
    
    /// <summary>
    /// User notes about this photo
    /// </summary>
    public string? Notes { get; set; }
    
    // Navigation property
    public Listing Listing { get; set; } = null!;
}
```

### DbContext Configuration

```csharp
// src/RealEstate.Infrastructure/RealEstateDbContext.cs

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    
    modelBuilder.Entity<UserListingPhoto>(entity =>
    {
        entity.ToTable("user_listing_photos", "re_realestate");
        
        entity.HasKey(e => e.Id);
        
        entity.Property(e => e.StoredUrl)
            .HasMaxLength(500)
            .IsRequired();
        
        entity.Property(e => e.OriginalFileName)
            .HasMaxLength(255)
            .IsRequired();
        
        entity.Property(e => e.Notes)
            .HasMaxLength(1000);
        
        entity.HasOne(e => e.Listing)
            .WithMany()
            .HasForeignKey(e => e.ListingId)
            .OnDelete(DeleteBehavior.Cascade);
        
        entity.HasIndex(e => e.ListingId);
        entity.HasIndex(e => e.UploadedAt);
    });
}
```

### Migration

```bash
dotnet ef migrations add AddUserListingPhotos --project src/RealEstate.Infrastructure --startup-project src/RealEstate.Api
```

---

## API Endpoints

### Upload User Photos

```csharp
// src/RealEstate.Api/Endpoints/UserPhotoEndpoints.cs

public static class UserPhotoEndpoints
{
    public static RouteGroupBuilder MapUserPhotoEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{listingId:guid}/my-photos", UploadPhotosAsync)
            .WithName("UploadUserPhotos")
            .DisableAntiforgery();  // For file uploads
        
        group.MapGet("/{listingId:guid}/my-photos", GetUserPhotosAsync)
            .WithName("GetUserPhotos");
        
        group.MapDelete("/{listingId:guid}/my-photos/{photoId:guid}", DeleteUserPhotoAsync)
            .WithName("DeleteUserPhoto");
        
        return group;
    }
    
    private static async Task<Ok<List<UserListingPhotoDto>>> UploadPhotosAsync(
        Guid listingId,
        IFormFileCollection files,
        [FromServices] IStorageService storageService,
        [FromServices] RealEstateDbContext dbContext,
        [FromServices] ILogger<Program> logger,
        CancellationToken ct)
    {
        if (files.Count == 0)
            return TypedResults.Ok(new List<UserListingPhotoDto>());
        
        // Verify listing exists
        var listing = await dbContext.Listings
            .FirstOrDefaultAsync(l => l.Id == listingId, ct);
        
        if (listing == null)
            throw new InvalidOperationException($"Listing {listingId} not found");
        
        var uploadedPhotos = new List<UserListingPhoto>();
        
        foreach (var file in files)
        {
            // Validate file type
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".heic", ".heif" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            
            if (!allowedExtensions.Contains(extension))
            {
                logger.LogWarning("Skipping unsupported file type: {FileName}", file.FileName);
                continue;
            }
            
            // Convert HEIC to JPEG if needed
            Stream uploadStream;
            string uploadFileName;
            
            if (extension is ".heic" or ".heif")
            {
                var jpegStream = new MemoryStream();
                await ConvertHeicToJpegAsync(file.OpenReadStream(), jpegStream, ct);
                jpegStream.Position = 0;
                uploadStream = jpegStream;
                uploadFileName = Path.ChangeExtension(file.FileName, ".jpg");
            }
            else
            {
                uploadStream = file.OpenReadStream();
                uploadFileName = file.FileName;
            }
            
            try
            {
                // Upload to storage
                var folder = $"listings/{listingId}/my_photos";
                var storedUrl = await storageService.UploadFileAsync(
                    uploadStream, 
                    uploadFileName, 
                    folder, 
                    ct);
                
                // Extract EXIF date if available
                var takenAt = await ExtractExifDateAsync(file.OpenReadStream(), ct) ?? DateTime.UtcNow;
                
                // Save to database
                var photo = new UserListingPhoto
                {
                    Id = Guid.NewGuid(),
                    ListingId = listingId,
                    StoredUrl = storedUrl,
                    OriginalFileName = file.FileName,
                    FileSizeBytes = file.Length,
                    TakenAt = takenAt,
                    UploadedAt = DateTime.UtcNow
                };
                
                dbContext.UserListingPhotos.Add(photo);
                uploadedPhotos.Add(photo);
                
                logger.LogInformation(
                    "Uploaded photo {FileName} for listing {ListingId}", 
                    file.FileName, 
                    listingId);
            }
            finally
            {
                if (uploadStream != file.OpenReadStream())
                    await uploadStream.DisposeAsync();
            }
        }
        
        await dbContext.SaveChangesAsync(ct);
        
        var dtos = uploadedPhotos.Select(p => new UserListingPhotoDto(
            p.Id,
            p.StoredUrl,
            p.OriginalFileName,
            p.FileSizeBytes,
            p.TakenAt,
            p.UploadedAt,
            p.Notes
        )).ToList();
        
        return TypedResults.Ok(dtos);
    }
    
    private static async Task<Ok<List<UserListingPhotoDto>>> GetUserPhotosAsync(
        Guid listingId,
        [FromServices] RealEstateDbContext dbContext,
        [FromServices] IStorageService storageService,
        CancellationToken ct)
    {
        var photos = await dbContext.UserListingPhotos
            .AsNoTracking()
            .Where(p => p.ListingId == listingId)
            .OrderBy(p => p.TakenAt)
            .ToListAsync(ct);
        
        var dtos = new List<UserListingPhotoDto>();
        
        foreach (var photo in photos)
        {
            // Get public URL from storage service
            var publicUrl = await storageService.GetFileUrlAsync(photo.StoredUrl, ct);
            
            dtos.Add(new UserListingPhotoDto(
                photo.Id,
                publicUrl ?? photo.StoredUrl,  // Fallback to stored URL
                photo.OriginalFileName,
                photo.FileSizeBytes,
                photo.TakenAt,
                photo.UploadedAt,
                photo.Notes
            ));
        }
        
        return TypedResults.Ok(dtos);
    }
    
    private static async Task<NoContent> DeleteUserPhotoAsync(
        Guid listingId,
        Guid photoId,
        [FromServices] RealEstateDbContext dbContext,
        [FromServices] IStorageService storageService,
        CancellationToken ct)
    {
        var photo = await dbContext.UserListingPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.ListingId == listingId, ct);
        
        if (photo == null)
            return TypedResults.NoContent();
        
        // Delete from storage
        await storageService.DeleteFileAsync(photo.StoredUrl, ct);
        
        // Delete from database
        dbContext.UserListingPhotos.Remove(photo);
        await dbContext.SaveChangesAsync(ct);
        
        return TypedResults.NoContent();
    }
    
    // Helper methods
    
    private static async Task<DateTime?> ExtractExifDateAsync(Stream stream, CancellationToken ct)
    {
        // TODO: Implement EXIF extraction using ImageSharp or similar
        // For now, return null (will use UploadedAt)
        return await Task.FromResult<DateTime?>(null);
    }
    
    private static async Task ConvertHeicToJpegAsync(
        Stream heicStream, 
        Stream jpegStream, 
        CancellationToken ct)
    {
        // Implemented in HEIC Support section below
        throw new NotImplementedException("HEIC conversion not yet implemented");
    }
}
```

### DTOs

```csharp
// src/RealEstate.Api/Contracts/UserPhotos/UserListingPhotoDto.cs

public record UserListingPhotoDto(
    Guid Id,
    string Url,  // Public URL for display
    string OriginalFileName,
    long FileSizeBytes,
    DateTime TakenAt,
    DateTime UploadedAt,
    string? Notes
);
```

---

## Blazor UI Components

### ListingDetail.razor Updates

```razor
@page "/listing/{Id:guid}"
@using RealEstate.Api.Contracts.Listings
@using RealEstate.Api.Contracts.UserPhotos
@inject IListingService ListingService
@inject ISnackbar Snackbar
@inject NavigationManager Navigation

<!-- Existing listing detail content -->

@if (_listing?.UserStatus == "Visited")
{
    <MudPaper Class="pa-4 mt-4">
        <MudText Typo="Typo.h6" Class="mb-2">Moje fotky z prohlídky</MudText>
        
        <MudFileUpload T="IReadOnlyList<IBrowserFile>" 
                       Accept=".jpg,.jpeg,.png,.heic,.heif"
                       OnFilesChanged="OnFilesSelectedAsync"
                       FilesChanged="OnFilesSelectedAsync"
                       MaximumFileCount="20"
                       Context="uploadContext">
            <ActivatorContent>
                <MudButton Variant="Variant.Filled" 
                           Color="Color.Primary"
                           StartIcon="@Icons.Material.Filled.PhotoCamera"
                           Disabled="_uploadInProgress">
                    @if (_uploadInProgress)
                    {
                        <MudProgressCircular Size="Size.Small" Indeterminate="true" Class="mr-2" />
                        <span>Nahrávám...</span>
                    }
                    else
                    {
                        <span>Přidat fotky z prohlídky</span>
                    }
                </MudButton>
            </ActivatorContent>
        </MudFileUpload>
        
        @if (_myPhotos.Any())
        {
            <MudGrid Class="mt-4">
                @foreach (var photo in _myPhotos)
                {
                    <MudItem xs="6" sm="4" md="3">
                        <MudCard>
                            <MudCardMedia Image="@photo.Url" Height="200" />
                            <MudCardContent>
                                <MudText Typo="Typo.caption">
                                    @photo.TakenAt.ToString("dd.MM.yyyy HH:mm")
                                </MudText>
                                <MudText Typo="Typo.caption" Color="Color.Secondary">
                                    @FormatFileSize(photo.FileSizeBytes)
                                </MudText>
                            </MudCardContent>
                            <MudCardActions>
                                <MudIconButton Icon="@Icons.Material.Filled.Delete"
                                               Color="Color.Error"
                                               Size="Size.Small"
                                               OnClick="@(() => DeletePhotoAsync(photo.Id))" />
                            </MudCardActions>
                        </MudCard>
                    </MudItem>
                }
            </MudGrid>
        }
        else
        {
            <MudAlert Severity="Severity.Info" Class="mt-2">
                Zatím jste nenahrál/a žádné fotky z prohlídky.
            </MudAlert>
        }
    </MudPaper>
}

@code {
    [Parameter]
    public Guid Id { get; set; }
    
    private ListingDetailDto? _listing;
    private List<UserListingPhotoDto> _myPhotos = new();
    private bool _uploadInProgress = false;
    
    protected override async Task OnInitializedAsync()
    {
        await LoadListingAsync();
        
        if (_listing?.UserStatus == "Visited")
        {
            await LoadMyPhotosAsync();
        }
    }
    
    private async Task LoadListingAsync()
    {
        try
        {
            _listing = await ListingService.GetByIdAsync(Id);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Chyba při načítání inzerátu: {ex.Message}", Severity.Error);
        }
    }
    
    private async Task LoadMyPhotosAsync()
    {
        try
        {
            _myPhotos = await ListingService.GetMyPhotosAsync(Id);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Chyba při načítání fotek: {ex.Message}", Severity.Error);
        }
    }
    
    private async Task OnFilesSelectedAsync(InputFileChangeEventArgs e)
    {
        if (e.FileCount == 0 || _uploadInProgress)
            return;
        
        _uploadInProgress = true;
        
        try
        {
            using var content = new MultipartFormDataContent();
            
            foreach (var file in e.GetMultipleFiles(20))
            {
                // Max 10MB per file
                var maxSize = 10 * 1024 * 1024;
                
                if (file.Size > maxSize)
                {
                    Snackbar.Add($"Soubor {file.Name} je příliš velký (max 10MB)", Severity.Warning);
                    continue;
                }
                
                var fileContent = new StreamContent(file.OpenReadStream(maxSize));
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
                content.Add(fileContent, "files", file.Name);
            }
            
            var uploadedPhotos = await ListingService.UploadMyPhotosAsync(Id, content);
            
            _myPhotos.AddRange(uploadedPhotos);
            
            Snackbar.Add($"Nahráno {uploadedPhotos.Count} fotek", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Chyba při nahrávání fotek: {ex.Message}", Severity.Error);
        }
        finally
        {
            _uploadInProgress = false;
        }
    }
    
    private async Task DeletePhotoAsync(Guid photoId)
    {
        var confirm = await DialogService.ShowMessageBox(
            "Smazat fotku?",
            "Opravdu chcete tuto fotku smazat?",
            yesText: "Smazat",
            cancelText: "Zrušit");
        
        if (confirm != true)
            return;
        
        try
        {
            await ListingService.DeleteMyPhotoAsync(Id, photoId);
            _myPhotos.RemoveAll(p => p.Id == photoId);
            
            Snackbar.Add("Fotka smazána", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Chyba při mazání fotky: {ex.Message}", Severity.Error);
        }
    }
    
    private string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024} KB";
        
        return $"{bytes / (1024 * 1024)} MB";
    }
}
```

### Service Methods

```csharp
// src/RealEstate.Api/Services/IListingService.cs

public interface IListingService
{
    // Existing methods...
    
    Task<List<UserListingPhotoDto>> GetMyPhotosAsync(Guid listingId, CancellationToken ct = default);
    Task<List<UserListingPhotoDto>> UploadMyPhotosAsync(Guid listingId, MultipartFormDataContent content, CancellationToken ct = default);
    Task DeleteMyPhotoAsync(Guid listingId, Guid photoId, CancellationToken ct = default);
}
```

---

## HEIC Support

iPhone photos are typically `.heic` (HEIF format). Blazor `IBrowserFile` can read them, but conversion to JPEG is needed for wider compatibility.

### Option 1: Server-Side Conversion (Recommended)

**NuGet Package:**
```xml
<PackageReference Include="Magick.NET-Q16-AnyCPU" Version="13.6.0" />
```

**Implementation:**
```csharp
using ImageMagick;

private static async Task ConvertHeicToJpegAsync(
    Stream heicStream, 
    Stream jpegStream, 
    CancellationToken ct)
{
    using var image = new MagickImage(heicStream);
    
    // Set JPEG quality
    image.Quality = 85;
    image.Format = MagickFormat.Jpeg;
    
    await image.WriteAsync(jpegStream, ct);
}
```

### Option 2: Client-Side Conversion (Browser)

**JavaScript Interop:**
```javascript
// wwwroot/js/heicConverter.js
window.heicConverter = {
    convertToJpeg: async function (file) {
        // Use canvas API or heic2any library
        const heic2any = (await import('heic2any')).default;
        
        const convertedBlob = await heic2any({
            blob: file,
            toType: 'image/jpeg',
            quality: 0.85
        });
        
        return convertedBlob;
    }
};
```

**Recommendation:** Use server-side conversion for simplicity and consistency.

---

## Security Considerations

### 1. File Upload Validation

```csharp
// Validate file extension
var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".heic", ".heif" };
var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

if (!allowedExtensions.Contains(extension))
    throw new InvalidOperationException("Unsupported file type");

// Validate file size (max 10MB)
if (file.Length > 10 * 1024 * 1024)
    throw new InvalidOperationException("File too large (max 10MB)");

// Validate MIME type (basic check, can be spoofed)
var allowedMimeTypes = new[] { "image/jpeg", "image/png", "image/heic", "image/heif" };
if (!allowedMimeTypes.Contains(file.ContentType))
    throw new InvalidOperationException("Invalid MIME type");
```

### 2. OAuth Token Storage

**NEVER commit secrets to git!**

- Development: Use .NET User Secrets
- Production: Use environment variables or Azure Key Vault

```bash
# User Secrets (development)
dotnet user-secrets init --project src/RealEstate.Api
dotnet user-secrets set "Storage:GoogleDrive:ClientSecret" "GOCSPX-xxx"
dotnet user-secrets set "Storage:GoogleDrive:RefreshToken" "1//xxx"

# Environment Variables (production)
export Storage__GoogleDrive__ClientSecret="xxx"
export Storage__GoogleDrive__RefreshToken="xxx"
```

### 3. Storage Provider Permissions

**Google Drive:**
- Use OAuth 2.0 with minimal scopes: `drive.file` (only access files created by app)
- Avoid `drive` scope (full access)

**OneDrive:**
- Use delegated permissions: `Files.ReadWrite` (user's files only)
- Avoid application permissions unless service account needed

---

## Implementation Priority

### Phase 1: Local Storage (MVP)
**Estimate:** 3 SP

1. ✅ Create `IStorageService` interface
2. ✅ Implement `LocalStorageService`
3. ✅ Add `UserListingPhoto` entity + migration
4. ✅ Implement upload/get/delete API endpoints
5. ✅ Test with Postman/curl

**Deliverables:**
- Local file uploads working
- Database persistence
- API endpoints functional

---

### Phase 2: Blazor UI
**Estimate:** 5 SP

1. ✅ Add `MudFileUpload` to ListingDetail.razor
2. ✅ Implement upload progress indicator
3. ✅ Photo gallery with MudGrid
4. ✅ Delete photo confirmation dialog
5. ✅ Error handling with Snackbar

**Deliverables:**
- User can upload photos from Mac/iPhone
- Photos displayed in gallery
- Delete functionality

---

### Phase 3: HEIC Conversion
**Estimate:** 3 SP

1. ✅ Add Magick.NET package
2. ✅ Implement `ConvertHeicToJpegAsync`
3. ✅ Test with iPhone photos
4. ✅ EXIF date extraction (optional)

**Deliverables:**
- iPhone .heic photos automatically converted to .jpg
- Original filename preserved

---

### Phase 4: Google Drive OAuth Setup
**Estimate:** 8 SP

1. ✅ Create Google Cloud Project
2. ✅ Enable Drive API
3. ✅ Create OAuth 2.0 credentials
4. ✅ Implement one-time authorization flow
5. ✅ Store refresh token in secrets
6. ✅ Test token refresh mechanism

**Deliverables:**
- OAuth flow documented
- Refresh token stored securely
- Access token auto-refresh working

---

### Phase 5: Google Drive Implementation
**Estimate:** 8 SP

1. ✅ Add Google.Apis.Drive.v3 NuGet
2. ✅ Implement `GoogleDriveStorageService`
3. ✅ Folder creation logic
4. ✅ Public URL generation
5. ✅ Error handling (quota, network)
6. ✅ Integration tests

**Deliverables:**
- Google Drive uploads working
- Public URLs accessible
- Error handling robust

---

### Phase 6: OneDrive Implementation
**Estimate:** 8 SP

1. ✅ Create Azure AD app registration
2. ✅ Add Microsoft.Graph NuGet
3. ✅ Implement `OneDriveStorageService`
4. ✅ OAuth setup similar to Google
5. ✅ Integration tests

**Deliverables:**
- OneDrive uploads working
- Alternative to Google Drive

---

## Total Estimate

- **Phase 1 (Local):** 3 SP
- **Phase 2 (UI):** 5 SP
- **Phase 3 (HEIC):** 3 SP
- **Phase 4 (Google OAuth):** 8 SP
- **Phase 5 (Google Drive):** 8 SP
- **Phase 6 (OneDrive):** 8 SP

**Total:** 35 SP (~5-7 weeks)

---

## Testing Checklist

Before deployment:

- [ ] Local storage: Upload 5 photos, verify files in `wwwroot/uploads`
- [ ] Database: Verify `user_listing_photos` rows created
- [ ] API: Test all 3 endpoints with Postman
- [ ] UI: Upload from Mac, verify gallery display
- [ ] HEIC: Upload iPhone photo, verify JPEG conversion
- [ ] Delete: Verify file deleted from storage AND database
- [ ] Error handling: Test upload without listing, oversized file
- [ ] Google Drive: OAuth flow, upload, public URL
- [ ] OneDrive: OAuth flow, upload, share link
- [ ] Security: Refresh token not in git, secrets in User Secrets

---

## References

- **Google Drive API:** https://developers.google.com/drive/api/v3/about-sdk
- **Microsoft Graph:** https://learn.microsoft.com/en-us/graph/api/driveitem-put-content
- **OAuth 2.0 Flow:** https://oauth.net/2/grant-types/authorization-code/
- **Magick.NET:** https://github.com/dlemstra/Magick.NET
- **MudBlazor FileUpload:** https://mudblazor.com/components/fileupload

---

**Document Version:** 1.0  
**Last Updated:** 22. února 2026  
**Status:** Ready for Implementation
