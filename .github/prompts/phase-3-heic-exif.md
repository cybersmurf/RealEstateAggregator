# Phase 3: HEIC Conversion & EXIF Extraction

**Status:** Ready to implement  
**Dependencies:** Phase 2 must be complete  
**Priority:** Medium (nice-to-have for Mac users)

## Objective
- Convert HEIC/HEIF images to JPEG
- Extract EXIF metadata (taken_at timestamp)
- Update UserListingPhoto entity with extracted data

## Implementation Strategy

### 1. Add Dependencies
```bash
dotnet add src/RealEstate.Api package Magick.NET
dotnet add src/RealEstate.Api package MetadataExtractor
```

### 2. Image Processing Service
Create `src/RealEstate.Api/Services/ImageProcessingService.cs`:

```csharp
public interface IImageProcessingService
{
    Task<(byte[] Data, string MimeType)> ConvertHeicToJpegAsync(
        byte[] inputData,
        CancellationToken ct = default);

    DateTime? ExtractExifDateAsync(byte[] imageData);
}

public sealed class ImageProcessingService(ILogger<ImageProcessingService> logger) 
    : IImageProcessingService
{
    public async Task<(byte[] Data, string MimeType)> ConvertHeicToJpegAsync(
        byte[] inputData,
        CancellationToken ct = default)
    {
        return await Task.Run(() => {
            using var image = new MagickImage(inputData);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 90;
            
            var data = image.ToByteArray();
            logger.LogInformation("Converted HEIC to JPEG: {Size} -> {NewSize}",
                inputData.Length, data.Length);
            
            return (data, "image/jpeg");
        }, ct);
    }

    public DateTime? ExtractExifDateAsync(byte[] imageData)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(
                new MemoryStream(imageData));

            var exifDir = directories.OfType<ExifSubIfdDirectory>()
                .FirstOrDefault();

            if (exifDir?.TryGetDateTime(
                ExifDirectoryBase.TagDateTime, out var dateTime) == true)
            {
                logger.LogInformation("Extracted EXIF date: {Date}", dateTime);
                return dateTime;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract EXIF data");
        }

        return null;
    }
}
```

### 3. Register Service
Update `src/RealEstate.Api/Program.cs`:
```csharp
builder.Services.AddScoped<IImageProcessingService, ImageProcessingService>();
```

### 4. Update UserPhotoEndpoints
Modify `UploadPhotosAsync` in `UserPhotoEndpoints.cs`:

```csharp
private static async Task<Ok<List<UserListingPhotoDto>>> UploadPhotosAsync(
    Guid listingId,
    IFormFileCollection files,
    [FromServices] IStorageService storageService,
    [FromServices] IImageProcessingService imageProcessor,
    [FromServices] RealEstateDbContext dbContext,
    [FromServices] ILogger<Program> logger,
    CancellationToken ct)
{
    // ... existing validation ...

    foreach (var file in files)
    {
        using var ms = new MemoryStream();
        await file.OpenReadStream(10 * 1024 * 1024).CopyToAsync(ms, ct);
        var fileData = ms.ToArray();

        // Convert HEIC if needed
        if (IsHeicFile(file.FileName))
        {
            (fileData, _) = await imageProcessor.ConvertHeicToJpegAsync(fileData, ct);
            // Update filename to .jpg
            file = new HeicConvertedFile(file, ".jpg");
        }

        // Extract EXIF date
        var takenAt = imageProcessor.ExtractExifDateAsync(fileData) ?? DateTime.UtcNow;

        // Store file and create record
        var storedUrl = await storageService.UploadFileAsync(
            "listings/" + listingId + "/my_photos",
            file.FileName,
            new MemoryStream(fileData),
            ct);

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

        uploadedPhotos.Add(photo);
        dbContext.UserListingPhotos.Add(photo);
    }

    await dbContext.SaveChangesAsync(ct);
    
    return TypedResults.Ok(uploadedPhotos
        .Select(p => new UserListingPhotoDto(
            p.Id,
            p.StoredUrl,
            p.OriginalFileName,
            p.FileSizeBytes,
            p.TakenAt,
            p.UploadedAt,
            p.Notes))
        .ToList());
}

private static bool IsHeicFile(string fileName) =>
    fileName.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
    fileName.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
```

## Benefits
- Auto-conversion for iPhone/Mac users (HEIC format)
- Automatic date extraction from photos
- Better UX without manual date picking
- Standardized JPEG output for web

## Testing
```bash
# Test with HEIC file from iPhone
curl -X POST http://localhost:5001/api/listings/{id}/my-photos \
  -F "files=@photo.heic"

# Verify:
# 1. File converted to .jpg
# 2. taken_at populated from EXIF
# 3. Uploaded successfully
```

## Notes
- HEIC conversion is async (no blocking)
- EXIF extraction is best-effort (fallback to upload time)
- Magick.NET requires ImageMagick library (platform-dependent)
- On Mac: install via Homebrew (`brew install imagemagick`)
- On Linux: `apt-get install libmagick++-dev`
- On Windows: Magick.NET NuGet includes binaries
