# Add Blazor Page Skill

## Description
Create a new Blazor Server page with MudBlazor components for RealEstateAggregator.

## Usage
```bash
gh copilot suggest "add blazor page for user favorites"
```

## Steps

1. **Create page file**
   - Location: `src/RealEstate.App/Components/Pages/{PageName}.razor`
   - Example: `Favorites.razor`

2. **Add page structure**
   ```razor
   @page "/favorites"
   @using RealEstate.Api.Contracts.Listings
   @inject IListingService ListingService
   @inject NavigationManager Navigation
   @inject ISnackbar Snackbar
   
   <PageTitle>My Favorites</PageTitle>
   
   <MudContainer MaxWidth="MaxWidth.Large" Class="mt-4">
       <MudText Typo="Typo.h4" Class="mb-4">My Favorites</MudText>
       
       @if (loading)
       {
           <MudProgressCircular Indeterminate="true" />
       }
       else if (!favorites.Any())
       {
           <MudAlert Severity="Severity.Info">
               No favorites yet. Browse listings to add some!
           </MudAlert>
       }
       else
       {
           <MudGrid>
               @foreach (var listing in favorites)
               {
                   <MudItem xs="12" sm="6" md="4">
                       <MudCard>
                           <MudCardMedia Image="@listing.ThumbnailUrl" Height="200" />
                           <MudCardContent>
                               <MudText Typo="Typo.h6">@listing.Title</MudText>
                               <MudText Typo="Typo.body2" Color="Color.Primary">
                                   @listing.Price?.ToString("N0") Kč
                               </MudText>
                           </MudCardContent>
                           <MudCardActions>
                               <MudButton Variant="Variant.Text" 
                                          Color="Color.Primary"
                                          OnClick="@(() => ViewDetails(listing.Id))">
                                   View Details
                               </MudButton>
                               <MudIconButton Icon="@Icons.Material.Filled.Delete"
                                              Color="Color.Error"
                                              OnClick="@(() => RemoveFavorite(listing.Id))" />
                           </MudCardActions>
                       </MudCard>
                   </MudItem>
               }
           </MudGrid>
       }
   </MudContainer>
   
   @code {
       private List<ListingSummaryDto> favorites = new();
       private bool loading = true;
       
       protected override async Task OnInitializedAsync()
       {
           await LoadFavorites();
       }
       
       private async Task LoadFavorites()
       {
           loading = true;
           try
           {
               favorites = await ListingService.GetFavoritesAsync();
           }
           catch (Exception ex)
           {
               Snackbar.Add($"Error loading favorites: {ex.Message}", Severity.Error);
           }
           finally
           {
               loading = false;
           }
       }
       
       private void ViewDetails(Guid id)
       {
           Navigation.NavigateTo($"/listing/{id}");
       }
       
       private async Task RemoveFavorite(Guid id)
       {
           try
           {
               await ListingService.RemoveFavoriteAsync(id);
               favorites.RemoveAll(l => l.Id == id);
               Snackbar.Add("Removed from favorites", Severity.Success);
           }
           catch (Exception ex)
           {
               Snackbar.Add($"Error: {ex.Message}", Severity.Error);
           }
       }
   }
   ```

3. **Add to navigation menu**
   ```razor
   <!-- src/RealEstate.App/Components/Layout/NavMenu.razor -->
   <MudNavLink Href="/favorites" 
               Icon="@Icons.Material.Filled.Favorite"
               Match="NavLinkMatch.Prefix">
       Favorites
   </MudNavLink>
   ```

4. **Test page**
   - Navigate to http://localhost:5002/favorites
   - Verify layout, loading state, error handling
   - Test navigation and actions

## MudBlazor Type Parameters (Critical!)

MudBlazor 9.x requires explicit type parameters:

```razor
@* ✅ Correct *@
<MudChip T="string" Size="Size.Small">@item</MudChip>
<MudCarousel TData="object" Style="height:400px;">...</MudCarousel>
<MudTable T="ListingSummaryDto" Items="@items">...</MudTable>

@* ❌ Wrong - compile error *@
<MudChip Size="Size.Small">@item</MudChip>
<MudCarousel Style="height:400px;">...</MudCarousel>
```

## Common Patterns

### Loading State
```razor
@if (loading)
{
    <MudProgressCircular Indeterminate="true" />
}
else
{
    @* Content *@
}
```

### Empty State
```razor
@if (!items.Any())
{
    <MudAlert Severity="Severity.Info">No items found</MudAlert>
}
```

### Error Handling
```csharp
try
{
    await operation();
    Snackbar.Add("Success!", Severity.Success);
}
catch (Exception ex)
{
    Snackbar.Add($"Error: {ex.Message}", Severity.Error);
}
```

### Navigation
```csharp
Navigation.NavigateTo($"/listing/{id}");
```

### Confirmation Dialog
```razor
<MudButton OnClick="@(() => ShowConfirmDialog(item))">Delete</MudButton>

@code {
    private async Task ShowConfirmDialog(Item item)
    {
        var result = await DialogService.ShowMessageBox(
            "Confirm Delete",
            "Are you sure?",
            yesText: "Delete", cancelText: "Cancel");
        
        if (result == true)
        {
            await DeleteItem(item);
        }
    }
}
```

## Checklist
- [ ] Page created with @page directive
- [ ] Required services injected
- [ ] Loading state implemented
- [ ] Error handling with Snackbar feedback
- [ ] MudBlazor components have type parameters (T, TData)
- [ ] Added to NavMenu
- [ ] Tested in browser
- [ ] Responsive design (xs/sm/md breakpoints)

## Related Files
- `src/RealEstate.App/Components/Pages/`
- `src/RealEstate.App/Components/Layout/NavMenu.razor`
- `src/RealEstate.App/Components/_Imports.razor`

## Troubleshooting

**Error:** "CS0411: Cannot infer type arguments for MudChip"
- Solution: Add `T="string"` or appropriate type parameter

**Error:** "NavigationManager not found"
- Solution: Add `@inject NavigationManager Navigation`

**Error:** "Snackbar not working"
- Solution: Add `@inject ISnackbar Snackbar`

**Error:** "Page not rendering"
- Solution: Check @page route doesn't conflict with existing routes
