using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.UserPhotos;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Api.Endpoints;

public static class UserPhotoEndpoints
{
    public static RouteGroupBuilder MapUserPhotoEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{listingId:guid}/my-photos", UploadPhotosAsync)
            .WithName("UploadUserPhotos")
            .DisableAntiforgery();  // Required for file uploads

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
            .AsNoTracking()
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

            // Validate file size (max 10MB)
            if (file.Length > 10 * 1024 * 1024)
            {
                logger.LogWarning("Skipping file too large: {FileName} ({Size} bytes)", file.FileName, file.Length);
                continue;
            }

            try
            {
                // TODO: Convert HEIC to JPEG if needed (Phase 3)
                var uploadStream = file.OpenReadStream();
                var uploadFileName = file.FileName;

                // Upload to storage
                var folder = $"listings/{listingId}/my_photos";
                var storedUrl = await storageService.UploadFileAsync(
                    uploadStream,
                    uploadFileName,
                    folder,
                    ct);

                // TODO: Extract EXIF date if available (Phase 3)
                var takenAt = DateTime.UtcNow;

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
                    "Uploaded photo {FileName} for listing {ListingId} → {StoredUrl}",
                    file.FileName,
                    listingId,
                    storedUrl);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload photo {FileName}", file.FileName);
                throw;
            }
        }

        await dbContext.SaveChangesAsync(ct);

        // Map to DTOs with public URLs
        var dtos = new List<UserListingPhotoDto>();
        foreach (var photo in uploadedPhotos)
        {
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
        [FromServices] ILogger<Program> logger,
        CancellationToken ct)
    {
        var photo = await dbContext.UserListingPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.ListingId == listingId, ct);

        if (photo == null)
            return TypedResults.NoContent();

        try
        {
            // Delete from storage
            await storageService.DeleteFileAsync(photo.StoredUrl, ct);
            logger.LogInformation("Deleted photo from storage: {StoredUrl}", photo.StoredUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete photo from storage: {StoredUrl}", photo.StoredUrl);
            // Continue with database deletion even if storage delete fails
        }

        // Delete from database
        dbContext.UserListingPhotos.Remove(photo);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Deleted user photo {PhotoId} from listing {ListingId}", photoId, listingId);

        return TypedResults.NoContent();
    }
}
