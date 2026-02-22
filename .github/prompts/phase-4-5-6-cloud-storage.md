# Phase 4-6: Cloud Storage (Google Drive & OneDrive)

**Status:** Design complete, ready for implementation  
**Phases:** 4 (OAuth Setup), 5 (Google Drive), 6 (OneDrive)  
**Total Story Points:** 16 SP  
**Priority:** Low-Medium (local storage MVP complete)

## Overview
Three-phase implementation for cloud storage providers.

### Phase 4: OAuth 2.0 Setup (8 SP)

#### Google Drive
1. Create Google Cloud Project
2. Enable Drive API
3. Create OAuth 2.0 credentials (Desktop app)
4. Store credentials in User Secrets / Environment Variables

#### OneDrive
1. Register app in Azure AD
2. Configure API permissions (Files.ReadWrite)
3. Store Client ID + Secret

#### Configuration
```json
{
  "Storage": {
    "Provider": "Local|GoogleDrive|OneDrive",
    "GoogleDrive": {
      "ClientId": "xxx.apps.googleusercontent.com",
      "ClientSecret": "secret",
      "FolderName": "RealEstate Photos",
      "RefreshToken": "refresh_token_here"
    },
    "OneDrive": {
      "ClientId": "uuid",
      "ClientSecret": "secret",
      "TenantId": "common|tenant_id",
      "FolderPath": "/RealEstate Photos",
      "RefreshToken": "refresh_token_here"
    }
  }
}
```

### Phase 5: GoogleDriveStorageService (5 SP)

**File:** `src/RealEstate.Infrastructure/Storage/GoogleDriveStorageService.cs`

```csharp
public sealed class GoogleDriveStorageService : IStorageService
{
    public async Task<string> UploadFileAsync(
        string folder,
        string fileName,
        Stream fileStream,
        CancellationToken cancellationToken = default)
    {
        // 1. Authenticate with service account or user token
        // 2. Get or create folder in Google Drive
        // 3. Upload file to folder
        // 4. Make file public (or set sharing)
        // 5. Return public URL: https://drive.google.com/file/d/{fileId}/view
        
        throw new NotImplementedException();
    }

    public Task<string?> GetFileUrlAsync(
        string storedUrl,
        CancellationToken cancellationToken = default)
    {
        // Parse fileId from stored URL
        // Return public sharing link
        return Task.FromResult<string?>(storedUrl);
    }

    public async Task DeleteFileAsync(
        string storedUrl,
        CancellationToken cancellationToken = default)
    {
        // Parse fileId and delete from Google Drive
        throw new NotImplementedException();
    }
}
```

#### Required NuGet
```bash
dotnet add src/RealEstate.Infrastructure package Google.Apis.Drive.v3
```

#### Token Refresh Logic
- Store refresh token in configuration
- Auto-refresh when token expires (Google API client handles this)
- Option for manual token refresh via admin endpoint

### Phase 6: OneDriveStorageService (3 SP)

**File:** `src/RealEstate.Infrastructure/Storage/OneDriveStorageService.cs`

```csharp
public sealed class OneDriveStorageService : IStorageService
{
    public async Task<string> UploadFileAsync(
        string folder,
        string fileName,
        Stream fileStream,
        CancellationToken cancellationToken = default)
    {
        // 1. Authenticate with Microsoft Graph
        // 2. Get or create folder in OneDrive
        // 3. Upload file to folder
        // 4. Create public sharing link (or use app-owned)
        // 5. Return sharing link: https://graph.microsoft.com/v1.0/drives/{driveId}/items/{itemId}
        
        throw new NotImplementedException();
    }

    public Task<string?> GetFileUrlAsync(
        string storedUrl,
        CancellationToken cancellationToken = default)
    {
        // Parse itemId and return webUrl
        return Task.FromResult<string?>(storedUrl);
    }

    public async Task DeleteFileAsync(
        string storedUrl,
        CancellationToken cancellationToken = default)
    {
        // Parse itemId and delete from OneDrive
        throw new NotImplementedException();
    }
}
```

#### Required NuGet
```bash
dotnet add src/RealEstate.Infrastructure package Microsoft.Graph
```

## Implementation Order

```
Phase 4: OAuth Setup
├── Google Cloud Project + Drive API ✓
├── Azure AD App Registration ✓
└── Configuration schema ✓

Phase 5: Google Drive Implementation
├── Service class
├── Authentication + token refresh
├── Upload/Download/Delete logic
├── Unit tests
└── Integration tests

Phase 6: OneDrive Implementation
├── Service class
├── Authentication + token refresh
├── Upload/Download/Delete logic
├── Unit tests
└── Integration tests
```

## Configuration Examples

### Local Development (using User Secrets)
```bash
dotnet user-secrets init --project src/RealEstate.Api
dotnet user-secrets set "Storage:Provider" "GoogleDrive"
dotnet user-secrets set "Storage:GoogleDrive:ClientId" "xxx.apps.googleusercontent.com"
dotnet user-secrets set "Storage:GoogleDrive:ClientSecret" "..."
dotnet user-secrets set "Storage:GoogleDrive:RefreshToken" "..."
```

### Production (using Environment Variables)
```bash
export STORAGE__PROVIDER=OneDrive
export STORAGE__ONEDRIVE__CLIENTID=uuid
export STORAGE__ONEDRIVE__CLIENTSECRET=secret
export STORAGE__ONEDRIVE__REFRESHTOKEN=token
```

## Testing Strategy

1. **Unit Tests**
   - Mock Google/Microsoft API clients
   - Test token refresh logic
   - Test path/URL construction

2. **Integration Tests**
   - Real credentials (test account)
   - Upload test file
   - Verify file exists in cloud
   - Download and verify content
   - Delete and verify removal

3. **Manual Testing**
   - Upload from Blazor UI
   - Verify file in Google Drive / OneDrive web interface
   - Download via public link
   - Delete from UI and verify cloud removal

## Migration Path

For users with existing photos in Local storage:
```csharp
// Migration endpoint: POST /api/admin/migrate-storage
// 1. Read all files from LocalStorageService
// 2. Upload each to new provider (GoogleDrive/OneDrive)
// 3. Update UserListingPhoto.stored_url for each
// 4. Delete from local storage (optional, keep backup)
```

## Quota Considerations

- **Google Drive:** Team Drive (unlimited for organizations)
- **OneDrive:** 1TB per user (upgrade with Microsoft 365)
- **Local:** Limited by server disk space
- Design to handle HTTP 429 (rate limit) gracefully

## Security Notes

- Never commit credentials to git (use User Secrets / Environment Variables)
- OAuth tokens should expire and refresh automatically
- Consider encryption for token storage
- OneDrive permissions should be minimal (Files.ReadWrite.All problematic)
- Google Drive: Use restricted OAuth scopes

## Future Enhancements

- S3/MinIO support for self-hosted object storage
- Azure Blob Storage support
- CloudFront CDN distribution for faster download
- Backup/sync strategy between providers
- Automatic cleanup of old versions
