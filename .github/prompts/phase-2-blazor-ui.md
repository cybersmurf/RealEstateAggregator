# Phase 2: Blazor UI - User Photo Upload Component

**Status:** Ready to implement  
**Commits Completed:** cde8bff (Phase 1 infrastructure), 9174cf7 (Phase 1 fixes)  
**Services Running:** ✅ API (5001), ✅ App (5002)

## Context

Phase 1 (Local Storage Backend) is complete:
- IStorageService abstraction working
- LocalStorageService fully functional
- UserPhotoEndpoints API: POST/GET/DELETE all tested
- Database: user_listing_photos table ready

## Phase 2 Requirements

### Feature: Blazor UI Component for Photo Upload
Add photo upload & gallery functionality to ListingDetail page.

### Acceptance Criteria
- [ ] MudFileUpload component on ListingDetail page
- [ ] Condition: Only show when `UserListingState == "Visited"`
- [ ] Multi-file upload (max 10 photos, 10MB each)
- [ ] Photo gallery with MudGrid + MudCard
- [ ] Delete confirmation dialog
- [ ] Upload progress indicator
- [ ] Snackbar success/error feedback
- [ ] Responsive design (mobile-friendly)

### Component Location
- **File:** `src/RealEstate.App/Components/Pages/ListingDetail.razor`
- **Section:** Add new MudGrid section after listing details (before existing sections)
- **Visibility:** Conditional on `listing.UserListingState == "Visited"`

### Implementation Details

#### Data Binding
```razor
@foreach (var photo in uploadedPhotos)
{
    <MudCard Class="mb-3">
        <MudCardMedia Image="@photo.Url" Height="300" />
        <MudCardContent>
            <MudText Typo="Typo.body2">@photo.OriginalFileName</MudText>
            <MudText Typo="Typo.caption">@GetFileSizeText(photo.FileSizeBytes)</MudText>
            <MudText Typo="Typo.caption">@photo.UploadedAt.ToString("g")</MudText>
        </MudCardContent>
        <MudCardActions>
            <MudButton Variant="Variant.Text" Color="Color.Error" 
                OnClick="@(() => ConfirmDelete(photo.Id))">Delete</MudButton>
        </MudCardActions>
    </MudCard>
}
```

#### File Upload Handler
```csharp
private async Task OnFilesSelectedAsync(InputFileChangeEventArgs e)
{
    try
    {
        using var content = new MultipartFormDataContent();
        foreach (var file in e.GetMultipleFiles(10))
        {
            using var ms = new MemoryStream();
            await file.OpenReadStream(10 * 1024 * 1024).CopyToAsync(ms);
            ms.Position = 0;
            content.Add(new StreamContent(ms), "files", file.Name);
        }

        var response = await HttpClient.PostAsync(
            $"/api/listings/{listingId}/my-photos",
            content);

        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            uploadedPhotos = JsonSerializer.Deserialize<List<UserListingPhotoDto>>(json) ?? [];
            Snackbar.Add("Photos uploaded successfully", Severity.Success);
        }
        else
        {
            Snackbar.Add("Upload failed", Severity.Error);
        }
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Photo upload error");
        Snackbar.Add($"Error: {ex.Message}", Severity.Error);
    }
}
```

#### Delete With Confirmation
```csharp
private async Task ConfirmDelete(Guid photoId)
{
    var result = await DialogService.ShowAsync<DeleteConfirmationDialog>(
        "Delete Photo",
        new DialogParameters<Guid> { { x => x.Id, photoId } });

    if (result?.Data == true)
    {
        await DeletePhotoAsync(photoId);
    }
}

private async Task DeletePhotoAsync(Guid photoId)
{
    try
    {
        var response = await HttpClient.DeleteAsync(
            $"/api/listings/{listingId}/my-photos/{photoId}");

        if (response.IsSuccessStatusCode)
        {
            uploadedPhotos.RemoveAll(p => p.Id == photoId);
            Snackbar.Add("Photo deleted", Severity.Success);
        }
        else
        {
            Snackbar.Add("Deletion failed", Severity.Error);
        }
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Photo deletion error");
        Snackbar.Add($"Error: {ex.Message}", Severity.Error);
    }
}
```

### UI Structure
```razor
@* Photo Upload Section - only when UserListingState == "Visited" *@
@if (listing?.UserListingState == "Visited")
{
    <MudGrid Class="my-6">
        <MudItem xs="12">
            <MudText Typo="Typo.h6">My Photos</MudText>
        </MudItem>

        <MudItem xs="12">
            <MudFileUpload T="IReadOnlyList<IBrowserFile>" 
                OnFilesChanged="@OnFilesSelectedAsync"
                Hidden="@false"
                Class="flex-1" 
                Accept=".jpg,.jpeg,.png,.heic,.heif"
                Multiple="true">
                <ButtonTemplate>
                    <MudButton HtmlTag="label"
                        Variant="Variant.Filled"
                        Color="Color.Primary"
                        StartIcon="@Icons.Material.Filled.CloudUpload"
                        for="fileupload">
                        Upload Photos
                    </MudButton>
                </ButtonTemplate>
            </MudFileUpload>
        </MudItem>

        @if (uploadedPhotos.Count > 0)
        {
            <MudItem xs="12">
                <MudGrid>
                    @foreach (var photo in uploadedPhotos)
                    {
                        <MudItem xs="12" sm="6" md="4">
                            <MudCard>
                                <MudCardMedia Image="@photo.Url" Height="250" />
                                <MudCardContent>
                                    <MudText Typo="Typo.body2" Class="text-truncate">
                                        @photo.OriginalFileName
                                    </MudText>
                                    <MudText Typo="Typo.caption">
                                        @GetFileSizeText(photo.FileSizeBytes)
                                    </MudText>
                                    <MudText Typo="Typo.caption">
                                        @photo.UploadedAt.ToString("g")
                                    </MudText>
                                </MudCardContent>
                                <MudCardActions>
                                    <MudButton Variant="Variant.Text" Color="Color.Error"
                                        Size="Size.Small"
                                        OnClick="@(() => ConfirmDelete(photo.Id))">
                                        Delete
                                    </MudButton>
                                </MudCardActions>
                            </MudCard>
                        </MudItem>
                    }
                </MudGrid>
            </MudItem>
        }
    </MudGrid>
}
```

### Services & Injection
```csharp
@inject HttpClient HttpClient
@inject ISnackbar Snackbar
@inject IDialogService DialogService
@inject ILogger<ListingDetail> Logger
```

### Helper Methods
```csharp
private string GetFileSizeText(long bytes)
{
    return bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024.0 * 1024):F2} MB"
    };
}
```

## Next Steps After Phase 2
1. Phase 3: HEIC conversion + EXIF extraction
2. Phase 4: Google Drive OAuth setup
3. Phase 5-6: Cloud storage implementations

## API Endpoints (Already Implemented)
- `POST /api/listings/{listingId}/my-photos` - Upload
- `GET /api/listings/{listingId}/my-photos` - Retrieve  
- `DELETE /api/listings/{listingId}/my-photos/{photoId}` - Delete

## Database
- Table: `re_realestate.user_listing_photos`
- Ready for use with all columns: id, listing_id, stored_url, original_file_name, file_size_bytes, taken_at, uploaded_at, notes

## Notes
- Phase 1 fixes: Endpoint registration bug fixed, path construction bug fixed
- All API tests passing
- Static file serving enabled on API
- Ready for UI implementation
