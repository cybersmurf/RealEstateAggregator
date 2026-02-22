# Filtering Architecture - Real Estate Aggregator

**Datum**: 22. února 2026  
**Verze**: 1.0  
**Pattern**: MudBlazor UI → DTO → PredicateBuilder → EF Core

---

## 📐 Architektura filtrování

### High-level flow

```
┌─────────────────────────────────────────────────────────────────┐
│                    BLAZOR CLIENT (Browser)                       │
│                                                                  │
│  ┌────────────────────────────────────────────────────────┐    │
│  │         ListingFilterViewModel                         │    │
│  │  • UI state (MudBlazor bindings)                       │    │
│  │  • SourceCodes, Municipality, Price, Area, Status      │    │
│  │  • SearchText, Page, PageSize                          │    │
│  └───────────────────────┬────────────────────────────────┘    │
│                          │ Map to DTO                           │
│  ┌───────────────────────▼────────────────────────────────┐    │
│  │         ListingFilterDto                               │    │
│  │  • Serializable API contract                          │    │
│  └───────────────────────┬────────────────────────────────┘    │
│                          │ HTTP POST                            │
└──────────────────────────┼──────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────┐
│                    ASP.NET CORE API                              │
│                                                                  │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  ListingEndpoint                                       │    │
│  │  POST /api/listings/search                             │    │
│  └───────────────────────┬────────────────────────────────┘    │
│                          │                                      │
│  ┌───────────────────────▼────────────────────────────────┐    │
│  │  ListingService                                        │    │
│  │  • BuildBasePredicate(filter)  → AND logic             │    │
│  │  • BuildSearchPredicate(text)  → OR logic              │    │
│  │  • Combine with predicate.And(searchPredicate)         │    │
│  └───────────────────────┬────────────────────────────────┘    │
│                          │                                      │
│  ┌───────────────────────▼────────────────────────────────┐    │
│  │  PredicateBuilder (LinqKit)                            │    │
│  │  • Expression<Func<Listing, bool>>                     │    │
│  │  • Dynamic AND/OR kombinace                            │    │
│  └───────────────────────┬────────────────────────────────┘    │
│                          │                                      │
│  ┌───────────────────────▼────────────────────────────────┐    │
│  │  EF Core Query                                         │    │
│  │  query.Where(predicate).Skip().Take()                  │    │
│  └───────────────────────┬────────────────────────────────┘    │
└──────────────────────────┼──────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────┐
│                    POSTGRESQL DATABASE                           │
│  SELECT * FROM listings WHERE ... ORDER BY ... LIMIT ... OFFSET │
└──────────────────────────────────────────────────────────────────┘
```

---

## 🎯 Princip "Thin Client, Smart Server"

### Proč tento pattern?

1. **Blazor = jen UI binding** - žádná query logika na klientu
2. **Server = všechna query logika** - dynamické PredicateBuilder výrazy
3. **Type-safe** - C# Expression Trees, compile-time check
4. **Performance** - EF Core generuje optimální SQL
5. **Testovatelné** - Unit testy pro predicate builders
6. **Copilot-friendly** - jasná struktura, snadno rozšiřitelné

---

## 📦 Komponenty systému

### 1. ListingFilterViewModel (Client)

**Účel**: UI state pro MudBlazor komponenty  
**Lokace**: `RealEstate.App/Models/ListingFilterViewModel.cs`

```csharp
public sealed class ListingFilterViewModel
{
    // Zdroj
    public List<string> SourceCodes { get; set; } = new();      // ["REMAX", "MMR"]
    
    // Lokalita
    public string? Region { get; set; }                         // "Jihomoravský kraj"
    public string? District { get; set; }                       // "Znojmo"
    public string? Municipality { get; set; }                   // "Znojmo"
    
    // Cena
    public decimal? PriceMin { get; set; }                      // 1_000_000
    public decimal? PriceMax { get; set; }                      // 5_000_000
    
    // Plochy
    public double? AreaBuiltUpMin { get; set; }                 // 80
    public double? AreaBuiltUpMax { get; set; }                 // 200
    public double? AreaLandMin { get; set; }                    // 500
    public double? AreaLandMax { get; set; }                    // 2000
    
    // Typ
    public string? PropertyType { get; set; }                   // "House"
    public string? OfferType { get; set; }                      // "Sale"
    
    // User state
    public string? UserStatus { get; set; }                     // "New", "Liked", "Disliked"
    
    // Fulltext search
    public string? SearchText { get; set; }                     // "plyn studna garáž"
    
    // Paging
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
```

**Vlastnosti**:
- Mutable - pro two-way binding v Blazoru
- Properties odpovídají `ListingFilterDto` na serveru
- Defaultní hodnoty pro paging

---

### 2. MudBlazor UI Components

**Účel**: Formulář pro nastavení filtrů  
**Lokace**: `RealEstate.App/Pages/Listings.razor`

#### Struktura stránky

```razor
@page "/listings"
@inject HttpClient Http
@inject NavigationManager Nav

<PageTitle>Realitní inzeráty</PageTitle>

<MudContainer MaxWidth="MaxWidth.ExtraLarge" Class="mt-4">
    <MudPaper Class="pa-4" Elevation="2">
        <MudText Typo="Typo.h4" GutterBottom="true">
            Vyhledávání nemovitostí
        </MudText>

        <!-- FILTRY -->
        <MudGrid Class="mt-4">
            <!-- Row 1: Zdroje, Lokalita -->
            <MudItem xs="12" md="6">
                <MudSelect T="string" Label="Zdroje realitních kanceláří" 
                           MultiSelection="true" 
                           @bind-SelectedValues="_filter.SourceCodes"
                           Variant="Variant.Outlined">
                    <MudSelectItem Value="@("REMAX")">RE/MAX</MudSelectItem>
                    <MudSelectItem Value="@("MMR")">M&amp;M Reality</MudSelectItem>
                    <MudSelectItem Value="@("PRODEJMETO")">Prodejme.to</MudSelectItem>
                </MudSelect>
            </MudItem>
            
            <MudItem xs="12" md="6">
                <MudTextField @bind-Value="_filter.Municipality" 
                              Label="Obec / Lokalita" 
                              Variant="Variant.Outlined"
                              Placeholder="např. Znojmo, Praha 5" />
            </MudItem>

            <!-- Row 2: Cena -->
            <MudItem xs="12" md="6">
                <MudNumericField @bind-Value="_filter.PriceMin" 
                                 Label="Cena od (Kč)" 
                                 Variant="Variant.Outlined"
                                 Format="N0"
                                 HideSpinButtons="true" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudNumericField @bind-Value="_filter.PriceMax" 
                                 Label="Cena do (Kč)" 
                                 Variant="Variant.Outlined"
                                 Format="N0"
                                 HideSpinButtons="true" />
            </MudItem>

            <!-- Row 3: Plochy -->
            <MudItem xs="12" md="6">
                <MudNumericField @bind-Value="_filter.AreaBuiltUpMin" 
                                 Label="Plocha domu od (m²)" 
                                 Variant="Variant.Outlined"
                                 HideSpinButtons="true" />
            </MudItem>
            <MudItem xs="12" md="6">
                <MudNumericField @bind-Value="_filter.AreaLandMin" 
                                 Label="Pozemek od (m²)" 
                                 Variant="Variant.Outlined"
                                 HideSpinButtons="true" />
            </MudItem>

            <!-- Row 4: Typ, Stav -->
            <MudItem xs="12" md="6">
                <MudSelect T="string" Label="Typ nemovitosti" 
                           @bind-Value="_filter.PropertyType"
                           Variant="Variant.Outlined"
                           Clearable="true">
                    <MudSelectItem Value="">Vše</MudSelectItem>
                    <MudSelectItem Value="@("House")">Dům</MudSelectItem>
                    <MudSelectItem Value="@("Apartment")">Byt</MudSelectItem>
                    <MudSelectItem Value="@("Land")">Pozemek</MudSelectItem>
                    <MudSelectItem Value="@("Commercial")">Komerční</MudSelectItem>
                </MudSelect>
            </MudItem>
            
            <MudItem xs="12" md="6">
                <MudSelect T="string" Label="Můj stav" 
                           @bind-Value="_filter.UserStatus"
                           Variant="Variant.Outlined"
                           Clearable="true">
                    <MudSelectItem Value="">Vše</MudSelectItem>
                    <MudSelectItem Value="@("New")">Nové (neviděl jsem)</MudSelectItem>
                    <MudSelectItem Value="@("Liked")">Líbí se mi</MudSelectItem>
                    <MudSelectItem Value="@("Disliked")">Nechci</MudSelectItem>
                    <MudSelectItem Value="@("ToVisit")">K návštěvě</MudSelectItem>
                    <MudSelectItem Value="@("Visited")">Navštíveno</MudSelectItem>
                </MudSelect>
            </MudItem>

            <!-- Row 5: Fulltext -->
            <MudItem xs="12">
                <MudTextField @bind-Value="_filter.SearchText" 
                              Label="Fulltext hledání" 
                              Variant="Variant.Outlined"
                              Placeholder="např. 'plyn studna garáž terasa'" />
            </MudItem>

            <!-- Row 6: Actions -->
            <MudItem xs="12">
                <MudStack Row="true" Spacing="2">
                    <MudButton Color="Color.Primary" 
                               Variant="Variant.Filled" 
                               StartIcon="@Icons.Material.Filled.Search"
                               OnClick="SearchAsync">
                        Hledat
                    </MudButton>
                    <MudButton Color="Color.Secondary" 
                               Variant="Variant.Outlined" 
                               StartIcon="@Icons.Material.Filled.Refresh"
                               OnClick="ResetFiltersAsync">
                        Reset filtrů
                    </MudButton>
                </MudStack>
            </MudItem>
        </MudGrid>

        <!-- VÝSLEDKY -->
        <MudDivider Class="my-4" />

        @if (_isLoading)
        {
            <MudProgressLinear Indeterminate="true" />
        }
        else if (_items.Any())
        {
            <MudText Typo="Typo.body2" Class="mb-2">
                Nalezeno <strong>@_totalCount</strong> inzerátů (stránka @_filter.Page)
            </MudText>

            <MudTable Items="_items" 
                      Hover="true" 
                      Dense="true" 
                      Striped="true"
                      Elevation="0">
                <HeaderContent>
                    <MudTh>Zdroj</MudTh>
                    <MudTh>Titulek</MudTh>
                    <MudTh>Lokalita</MudTh>
                    <MudTh Style="text-align: right;">Cena</MudTh>
                    <MudTh Style="text-align: right;">Plocha</MudTh>
                    <MudTh Style="text-align: right;">Pozemek</MudTh>
                    <MudTh>Stav</MudTh>
                    <MudTh></MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd>
                        <MudChip Size="Size.Small" Color="Color.Info">
                            @context.SourceName
                        </MudChip>
                    </MudTd>
                    <MudTd>
                        <MudText Typo="Typo.body2">@context.Title</MudText>
                    </MudTd>
                    <MudTd>@context.LocationText</MudTd>
                    <MudTd Style="text-align: right;">
                        <strong>@(context.Price?.ToString("N0") ?? "-")</strong> Kč
                    </MudTd>
                    <MudTd Style="text-align: right;">
                        @(context.AreaBuiltUp?.ToString("N0") ?? "-") m²
                    </MudTd>
                    <MudTd Style="text-align: right;">
                        @(context.AreaLand?.ToString("N0") ?? "-") m²
                    </MudTd>
                    <MudTd>
                        @if (context.UserStatus == "Liked")
                        {
                            <MudChip Size="Size.Small" Color="Color.Success">❤️ Líbí se</MudChip>
                        }
                        else if (context.UserStatus == "Disliked")
                        {
                            <MudChip Size="Size.Small" Color="Color.Error">👎 Nechci</MudChip>
                        }
                        else if (context.UserStatus == "ToVisit")
                        {
                            <MudChip Size="Size.Small" Color="Color.Warning">📍 Navštívit</MudChip>
                        }
                        else
                        {
                            <MudChip Size="Size.Small" Color="Color.Default">🆕 Nový</MudChip>
                        }
                    </MudTd>
                    <MudTd>
                        <MudButton Size="Size.Small" 
                                   Color="Color.Primary" 
                                   Variant="Variant.Text"
                                   OnClick="@(() => NavigateToDetail(context.Id))">
                            Detail
                        </MudButton>
                    </MudTd>
                </RowTemplate>
            </MudTable>

            <!-- PAGING -->
            <MudPagination Class="mt-4" 
                           Count="@GetTotalPages()" 
                           Selected="@_filter.Page"
                           SelectedChanged="OnPageChangedAsync" 
                           ShowFirstButton="true" 
                           ShowLastButton="true" />
        }
        else
        {
            <MudAlert Severity="Severity.Info">
                Žádné výsledky nenalezeny. Zkuste upravit filtry.
            </MudAlert>
        }
    </MudPaper>
</MudContainer>
```

#### Code-behind

```csharp
@code {
    private ListingFilterViewModel _filter = new();
    private List<ListingSummaryDto> _items = new();
    private int _totalCount = 0;
    private bool _isLoading = false;

    protected override async Task OnInitializedAsync()
    {
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            // Map ViewModel → DTO
            var dto = new ListingFilterDto
            {
                SourceCodes = _filter.SourceCodes,
                Region = _filter.Region,
                District = _filter.District,
                Municipality = _filter.Municipality,
                PriceMin = _filter.PriceMin,
                PriceMax = _filter.PriceMax,
                AreaBuiltUpMin = _filter.AreaBuiltUpMin,
                AreaBuiltUpMax = _filter.AreaBuiltUpMax,
                AreaLandMin = _filter.AreaLandMin,
                AreaLandMax = _filter.AreaLandMax,
                PropertyType = _filter.PropertyType,
                OfferType = _filter.OfferType,
                UserStatus = _filter.UserStatus,
                SearchText = _filter.SearchText,
                Page = _filter.Page,
                PageSize = _filter.PageSize
            };

            // POST to API
            var response = await Http.PostAsJsonAsync("api/listings/search", dto);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PagedResultDto<ListingSummaryDto>>();
            
            _items = result?.Items.ToList() ?? new();
            _totalCount = result?.TotalCount ?? 0;
        }
        catch (Exception ex)
        {
            // TODO: Error handling (Snackbar)
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private async Task ResetFiltersAsync()
    {
        _filter = new ListingFilterViewModel();
        await SearchAsync();
    }

    private async Task OnPageChangedAsync(int page)
    {
        _filter.Page = page;
        await SearchAsync();
    }

    private int GetTotalPages()
    {
        return (_totalCount + _filter.PageSize - 1) / _filter.PageSize;
    }

    private void NavigateToDetail(Guid id)
    {
        Nav.NavigateTo($"/listing/{id}");
    }
}
```

---

### 3. Server-Side PredicateBuilder Logic

**Účel**: Převést `ListingFilterDto` na EF Core expression  
**Lokace**: `RealEstate.Api/Services/ListingService.cs`

#### BuildBasePredicate (AND logic)

```csharp
private Expression<Func<Listing, bool>> BuildBasePredicate(ListingFilterDto filter)
{
    var predicate = PredicateBuilder.New<Listing>(true); // Start s "WHERE 1=1"

    // IsActive (vždy aktivní inzeráty)
    predicate = predicate.And(x => x.IsActive);

    // SourceCodes (IN)
    if (filter.SourceCodes?.Any() == true)
    {
        predicate = predicate.And(x => filter.SourceCodes.Contains(x.Source.Code));
    }

    // Lokalita
    if (!string.IsNullOrWhiteSpace(filter.Region))
        predicate = predicate.And(x => x.Region == filter.Region);

    if (!string.IsNullOrWhiteSpace(filter.District))
        predicate = predicate.And(x => x.District == filter.District);

    if (!string.IsNullOrWhiteSpace(filter.Municipality))
        predicate = predicate.And(x => x.Municipality != null && 
                                        x.Municipality.Contains(filter.Municipality));

    // Cena
    if (filter.PriceMin.HasValue)
        predicate = predicate.And(x => x.Price >= filter.PriceMin.Value);

    if (filter.PriceMax.HasValue)
        predicate = predicate.And(x => x.Price <= filter.PriceMax.Value);

    // Plocha zastavěná
    if (filter.AreaBuiltUpMin.HasValue)
        predicate = predicate.And(x => x.AreaBuiltUp >= (decimal)filter.AreaBuiltUpMin.Value);

    if (filter.AreaBuiltUpMax.HasValue)
        predicate = predicate.And(x => x.AreaBuiltUp <= (decimal)filter.AreaBuiltUpMax.Value);

    // Plocha pozemku
    if (filter.AreaLandMin.HasValue)
        predicate = predicate.And(x => x.AreaLand >= (decimal)filter.AreaLandMin.Value);

    if (filter.AreaLandMax.HasValue)
        predicate = predicate.And(x => x.AreaLand <= (decimal)filter.AreaLandMax.Value);

    // PropertyType
    if (!string.IsNullOrWhiteSpace(filter.PropertyType) &&
        Enum.TryParse<PropertyType>(filter.PropertyType, out var propType))
    {
        predicate = predicate.And(x => x.PropertyType == propType);
    }

    // OfferType
    if (!string.IsNullOrWhiteSpace(filter.OfferType) &&
        Enum.TryParse<OfferType>(filter.OfferType, out var offerType))
    {
        predicate = predicate.And(x => x.OfferType == offerType);
    }

    // UserStatus (filtrování podle UserListingState)
    if (!string.IsNullOrWhiteSpace(filter.UserStatus))
    {
        if (filter.UserStatus == "New")
        {
            // Inzeráty bez UserState nebo se stavem New
            predicate = predicate.And(x => 
                !x.UserStates.Any() || 
                x.UserStates.Any(us => us.Status == "New"));
        }
        else
        {
            predicate = predicate.And(x => 
                x.UserStates.Any(us => us.Status == filter.UserStatus));
        }
    }

    return predicate;
}
```

#### BuildSearchPredicate (OR logic pro fulltext)

```csharp
private Expression<Func<Listing, bool>> BuildSearchPredicate(string searchText)
{
    var keywords = searchText
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(k => k.Trim().ToLowerInvariant())
        .ToList();

    if (!keywords.Any())
        return PredicateBuilder.New<Listing>(true);

    // OR kombinace - každé klíčové slovo hledáme v Title nebo Description
    var searchPredicate = PredicateBuilder.New<Listing>(false); // Start s "WHERE 0=1"

    foreach (var keyword in keywords)
    {
        var keywordCopy = keyword; // Closure fix
        searchPredicate = searchPredicate.Or(x =>
            (x.Title != null && x.Title.ToLower().Contains(keywordCopy)) ||
            (x.Description != null && x.Description.ToLower().Contains(keywordCopy)) ||
            (x.LocationText != null && x.LocationText.ToLower().Contains(keywordCopy))
        );
    }

    return searchPredicate;
}
```

#### Kombinace v SearchAsync

```csharp
public async Task<PagedResultDto<ListingSummaryDto>> SearchAsync(
    ListingFilterDto filter,
    CancellationToken cancellationToken)
{
    var query = _repository.Query(); // IQueryable<Listing> s AsExpandable()

    // 1) Base predicate (AND kombinace všech filtrů)
    var predicate = BuildBasePredicate(filter);

    // 2) Search predicate (OR kombinace klíčových slov)
    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
        var searchPredicate = BuildSearchPredicate(filter.SearchText);
        predicate = predicate.And(searchPredicate); // AND (base) AND (search OR search OR ...)
    }

    // 3) Apply predicate
    query = query.Where(predicate);

    // 4) Count
    var totalCount = await query.CountAsync(cancellationToken);

    // 5) Sort
    query = query
        .OrderByDescending(x => x.FirstSeenAt)
        .ThenBy(x => x.Price);

    // 6) Page
    var skip = (filter.Page - 1) * filter.PageSize;
    var entities = await query
        .Skip(skip)
        .Take(filter.PageSize)
        .ToListAsync(cancellationToken);

    // 7) Project to DTO
    var items = entities.Select(MapToSummaryDto).ToList();

    return new PagedResultDto<ListingSummaryDto>
    {
        Items = items,
        Page = filter.Page,
        PageSize = filter.PageSize,
        TotalCount = totalCount
    };
}
```

---

## 🔍 Příklad generovaného SQL

Pro filtr:
```csharp
{
    SourceCodes = ["REMAX"],
    Municipality = "Znojmo",
    PriceMin = 2_000_000,
    PriceMax = 5_000_000,
    AreaLandMin = 500,
    SearchText = "plyn garáž"
}
```

EF Core vygeneruje:

```sql
SELECT l.*, s.*, p.*
FROM listings l
INNER JOIN sources s ON l.source_id = s.id
LEFT JOIN listing_photos p ON l.id = p.listing_id
WHERE l.is_active = TRUE
  AND s.code IN ('REMAX')
  AND l.municipality LIKE '%Znojmo%'
  AND l.price >= 2000000
  AND l.price <= 5000000
  AND l.area_land >= 500
  AND (
      LOWER(l.title) LIKE '%plyn%' OR LOWER(l.description) LIKE '%plyn%'
      OR LOWER(l.title) LIKE '%garáž%' OR LOWER(l.description) LIKE '%garáž%'
  )
ORDER BY l.first_seen_at DESC, l.price ASC
LIMIT 50 OFFSET 0;
```

**Performance**:
- EF Core překládá Expression Trees → optimální SQL
- PostgreSQL používá indexy (na `is_active`, `source_id`, `municipality`, `price`)
- Full-text search lze později upgradovat na PostgreSQL `tsvector`

---

## ✅ UX Best Practices

### 1. Auto-search vs. Manual search

**Varinta A: Manual (Button)**
```razor
<MudButton OnClick="SearchAsync">Hledat</MudButton>
```
✅ Kontrola nad počtem requestů  
✅ Lepší pro pomalé konexe  
❌ Extra klik

**Varianta B: Auto-search (Debounced)**
```csharp
private Timer? _debounceTimer;

private void OnFilterChanged()
{
    _debounceTimer?.Dispose();
    _debounceTimer = new Timer(async _ => await SearchAsync(), null, 500, Timeout.Infinite);
}
```
✅ Instant feedback  
❌ Více requestů na server

**Doporučení**: Pro MVP použít manual button, později přidat debounced search pro některé pole (např. SearchText).

---

### 2. Loading states

```razor
@if (_isLoading)
{
    <MudProgressLinear Indeterminate="true" />
}
```

Vždy zobrazit loading indicator během `SearchAsync()`.

---

### 3. Empty states

```razor
else if (!_items.Any())
{
    <MudAlert Severity="Severity.Info">
        Žádné výsledky. Zkuste upravit filtry.
    </MudAlert>
}
```

---

### 4. Persistence filtrů (Optional)

Query string parameters:
```csharp
protected override void OnInitialized()
{
    var uri = new Uri(Nav.Uri);
    var query = HttpUtility.ParseQueryString(uri.Query);
    
    _filter.Municipality = query["municipality"];
    _filter.PriceMin = decimal.TryParse(query["priceMin"], out var min) ? min : null;
    // ...
}

private void UpdateQueryString()
{
    var queryParams = new Dictionary<string, string?>
    {
        ["municipality"] = _filter.Municipality,
        ["priceMin"] = _filter.PriceMin?.ToString(),
        // ...
    };
    
    var url = Nav.GetUriWithQueryParameters(queryParams);
    Nav.NavigateTo(url, replace: true);
}
```

→ Umožní sdílení linků s filtry.

---

## 🧪 Testování

### Unit test: PredicateBuilder

```csharp
[Fact]
public async Task SearchAsync_WithPriceRange_ReturnsFilteredListings()
{
    // Arrange
    var filter = new ListingFilterDto
    {
        PriceMin = 2_000_000,
        PriceMax = 5_000_000
    };

    // Act
    var result = await _service.SearchAsync(filter, CancellationToken.None);

    // Assert
    result.Items.Should().AllSatisfy(x =>
    {
        x.Price.Should().BeGreaterOrEqualTo(2_000_000);
        x.Price.Should().BeLessOrEqualTo(5_000_000);
    });
}
```

### Integration test: E2E flow

```csharp
[Fact]
public async Task E2E_FilterAndPaging_WorksCorrectly()
{
    // 1. Seed DB s test data
    await SeedTestListings();

    // 2. Call API
    var response = await _httpClient.PostAsJsonAsync("api/listings/search", new ListingFilterDto
    {
        Municipality = "Znojmo",
        Page = 1,
        PageSize = 10
    });

    // 3. Assert
    response.Should().BeSuccessful();
    var result = await response.Content.ReadFromJsonAsync<PagedResultDto<ListingSummaryDto>>();
    result.Items.Should().HaveCount(10);
    result.TotalCount.Should().BeGreaterThan(10);
}
```

---

## 📚 Reference

### Dokumentace
- [MudBlazor Table](https://mudblazor.com/components/table)
- [LinqKit PredicateBuilder](https://github.com/scottksmith95/LINQKit)
- [EF Core + LinqKit](https://riptutorial.com/efcore-linqkit/learn/100006/predicate-builder)
- [Mitch Sellers - PredicateBuilder with EF Core](https://mitchelsellers.com/blog/article/using-predicatebuilder-with-ef-core-for-complex-queries)

### Příklady v projektu
- `RealEstate.Api/Services/ListingService.cs` - PredicateBuilder implementace
- `RealEstate.Api/Contracts/Listings/ListingFilterDto.cs` - DTO contract
- `RealEstate.App/Models/ListingFilterViewModel.cs` - UI state
- `RealEstate.App/Pages/Listings.razor` - MudBlazor UI

---

## 🚀 Rozšíření (Future)

### 1. Saved Filters (User Preferences)

```csharp
public class SavedFilter
{
    public Guid Id { get; set; }
    public string UserId { get; set; }
    public string Name { get; set; }
    public string FilterJson { get; set; } // JSON serialized ListingFilterDto
}
```

→ Uživatel si uloží často používané filtry.

### 2. Advanced Search (Range Sliders)

```razor
<MudRangeSlider @bind-Values="_priceRange" Min="0" Max="10_000_000" Step="100_000">
    Cena: @_priceRange.Item1.ToString("N0") - @_priceRange.Item2.ToString("N0") Kč
</MudRangeSlider>
```

### 3. Map-based Filtering

Integrace s Google Maps nebo OpenStreetMap:
- Kreslení polygonů na mapě
- Filtrování listings uvnitř polygonu
- PostGIS spatial queries

### 4. Full-Text Search Upgrade

PostgreSQL `tsvector`:
```sql
CREATE INDEX idx_listing_fts ON listings 
USING gin(to_tsvector('czech', title || ' ' || description));

SELECT * FROM listings
WHERE to_tsvector('czech', title || ' ' || description) 
      @@ to_tsquery('czech', 'plyn & garáž');
```

---

**Konec dokumentu**  
Pro implementační detaily viz BACKLOG.md → Sprint 1 - Filtering Implementation
