# Claude AI Instructions – RealEstateAggregator

**Project Type:** Full-stack web application with web scraping pipeline  
**Domain:** Czech real estate listing aggregation with semantic search  
**Your Role:** Senior full-stack engineer with expertise in .NET, Python, and PostgreSQL

---

## Project Overview

Real Estate Aggregator scrapes Czech real estate websites (REMAX, M&M Reality, Prodejme.to), stores listings in PostgreSQL with pgvector for semantic search, and provides a Blazor UI for browsing and AI analysis.

## Persistent Rules (Always Apply)

- MudBlazor 9 je primarni UI stack. Nezminuj MudBlazor 7, pokud nejde o historickou poznamku.
- Udrzuj verze stacku konzistentni napric README, QUICK_START, TECHNICAL_DESIGN, API_CONTRACTS, AI_SESSION_SUMMARY.
- Pri zmenach scrapingu aktualizuj souvisejici dokumentaci a instrukce, aby odpovidaly realnemu chovani.

### Technology Stack

| Layer | Technologies |
|-------|-------------|
| **Frontend** | Blazor Server + MudBlazor 9.x |
| **Backend** | .NET 10 + ASP.NET Core Minimal APIs |
| **Database** | PostgreSQL 15 + pgvector extension |
| **ORM** | EF Core 10 + EFCore.NamingConventions |
| **Scraper** | Python 3.12 + FastAPI + httpx + BeautifulSoup4 + asyncpg |
| **Container** | Docker Compose |

### Architecture Diagram

```
┌──────────────┐
│  Blazor UI   │ :5002
└──────┬───────┘
       │ HTTP
       ▼
┌──────────────┐
│   .NET API   │ :5001
└──────┬───────┘
       │ EF Core
       ▼
┌──────────────┐      ┌─────────────────┐
│ PostgreSQL   │◄─────│ Python Scraper  │ :8001
│  + pgvector  │:5432 │    FastAPI      │
└──────────────┘      └────────┬────────┘
                               │ httpx
                               ▼
                      ┌────────────────┐
                      │ External Sites │
                      │ (REMAX, MMR)   │
                      └────────────────┘
```

---

## Core Principles

### 1. Async-First Development
- **All I/O operations must be async** (database, HTTP, file system)
- Python: `async def`, `await`, `asyncio`
- C#: `async Task<T>`, `await`, `CancellationToken`

### 2. Robust Error Handling
```csharp
// C# - Always provide user feedback
try {
    var result = await service.DoSomethingAsync(cancellationToken);
    Snackbar.Add("Operation completed successfully", Severity.Success);
    return result;
} catch (Exception ex) {
    logger.LogError(ex, "Operation failed");
    Snackbar.Add($"Error: {ex.Message}", Severity.Error);
    throw;
}
```

```python
# Python - Log and re-raise for upstream handling
try:
    listing_id = await db.upsert_listing(listing_data)
    logger.info(f"Saved listing {listing_id}")
except Exception as exc:
    logger.error(f"Failed to save listing: {exc}", exc_info=True)
    raise
```

### 3. Database Conventions
- **Snake_case** for all PostgreSQL columns (via EFCore.NamingConventions)
- **Guid** primary keys (not int)
- **Enum mapping**: Czech values in scraper → English in database
  - Dům → House, Byt → Apartment
  - Prodej → Sale, Pronájem → Rent

### 4. Type Safety
```csharp
// C# - Use records for DTOs
public record ListingSummaryDto(
    Guid Id,
    string Title,
    decimal? Price,
    string LocationText,
    PropertyType PropertyType
);
```

```python
# Python - Always use type hints
def _parse_detail_page(self, html: str, item: Dict[str, Any]) -> Dict[str, Any]:
    """Parse detail page HTML and return structured data."""
    # Implementation
```

---

## Key Files & Patterns

### Database Access Pattern

**C# (EF Core):**
```csharp
// src/RealEstate.Api/Services/ListingService.cs
public class ListingService(
    RealEstateDbContext context,
    ILogger<ListingService> logger)
{
    public async Task<PagedResult<ListingSummaryDto>> SearchAsync(
        ListingFilterDto filter,
        CancellationToken ct)
    {
        var query = context.Listings
            .Where(l => l.IsActive)
            .AsNoTracking();
        
        if (!string.IsNullOrEmpty(filter.SearchQuery))
        {
            query = query.Where(l => 
                l.Title.Contains(filter.SearchQuery) ||
                l.Description.Contains(filter.SearchQuery)
            );
        }
        
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(l => new ListingSummaryDto(
                l.Id,
                l.Title,
                l.Price,
                l.LocationText,
                l.PropertyType
            ))
            .ToListAsync(ct);
        
        return new PagedResult<ListingSummaryDto>(items, total, filter.Page, filter.PageSize);
    }
}
```

**Python (asyncpg):**
```python
# scraper/core/database.py
async def upsert_listing(self, listing_data: Dict[str, Any]) -> UUID:
    """
    Upsert listing - UPDATE if exists (by source_id + external_id), INSERT if new.
    Returns listing UUID.
    """
    source = await self.get_source_by_code(listing_data["source_code"])
    if not source:
        raise ValueError(f"Source '{listing_data['source_code']}' not found")
    
    # Map Czech → English
    property_type_db = self.PROPERTY_TYPE_MAP.get(
        listing_data.get("property_type", "Ostatní"), 
        "Other"
    )
    
    async with self.acquire() as conn:
        existing = await conn.fetchrow(
            "SELECT id FROM re_realestate.listings WHERE source_id = $1 AND external_id = $2",
            source["id"], listing_data.get("external_id")
        )
        
        if existing:
            # UPDATE
            await conn.execute(
                "UPDATE re_realestate.listings SET title = $1, ... WHERE id = $2",
                listing_data.get("title"), existing["id"]
            )
            return existing["id"]
        else:
            # INSERT
            listing_id = uuid4()
            await conn.execute(
                "INSERT INTO re_realestate.listings (id, source_id, ...) VALUES ($1, $2, ...)",
                listing_id, source["id"], ...
            )
            return listing_id
```

### Scraping Pattern (Regex-Based)

```python
# scraper/core/scrapers/remax_scraper.py
class RemaxScraper:
    """
    Scraper for REMAX Czech Republic.
    
    Strategy:
    - httpx + BeautifulSoup for list pages (fast)
    - Regex selectors (robust against CSS changes)
    - Deduplication by external_id
    - Rate limiting (1 sec delay)
    """
    
    BASE_URL = "https://www.remax-czech.cz"
    SOURCE_CODE = "REMAX"
    
    def _parse_list_page(self, html: str) -> List[Dict[str, Any]]:
        soup = BeautifulSoup(html, "html.parser")
        results = []
        
        # Find all detail links
        for link in soup.select('a[href*="/reality/detail/"]'):
            href = link.get('href', '')
            
            # Extract external_id via regex
            match = re.search(r'/reality/detail/(\d+)/', href)
            if not match:
                continue
            
            external_id = match.group(1)
            detail_url = urljoin(self.BASE_URL, href)
            
            results.append({
                "external_id": external_id,
                "detail_url": detail_url,
                "title": link.get_text(strip=True)
            })
        
        # Deduplicate by external_id
        seen = set()
        unique = [r for r in results if not (r["external_id"] in seen or seen.add(r["external_id"]))]
        
        return unique
    
    def _parse_detail_page(self, html: str, item: Dict) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        
        # Title from <h1>
        title = soup.find('h1').get_text(strip=True) if soup.find('h1') else item["title"]
        
        # Price via regex
        price = None
        price_match = soup.find(string=re.compile(r'(\d[\d\s]+)\s*Kč'))
        if price_match:
            price_str = re.search(r'(\d[\d\s]+)', price_match).group(1)
            price = float(price_str.replace(' ', ''))
        
        # Property type inference
        title_lower = title.lower()
        if "dům" in title_lower or "vila" in title_lower:
            property_type = "Dům"
        elif "byt" in title_lower:
            property_type = "Byt"
        else:
            property_type = "Ostatní"
        
        return {
            "source_code": self.SOURCE_CODE,
            "external_id": item["external_id"],
            "url": item["detail_url"],
            "title": title,
            "price": price,
            "property_type": property_type,
            # ... more fields
        }
```

### Blazor Component Pattern

```razor
@* src/RealEstate.App/Components/Pages/Listings.razor *@
@page "/listings"
@inject IListingService ListingService
@inject NavigationManager Navigation
@inject ISnackbar Snackbar

<MudContainer MaxWidth="MaxWidth.ExtraLarge">
    <MudTable T="ListingSummaryDto" 
              Items="@listings" 
              Loading="@loading"
              OnRowClick="HandleRowClick">
        <HeaderContent>
            <MudTh>Title</MudTh>
            <MudTh>Price</MudTh>
            <MudTh>Location</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Title</MudTd>
            <MudTd>@context.Price?.ToString("N0") Kč</MudTd>
            <MudTd>@context.LocationText</MudTd>
            <MudTd>
                <MudButton OnClick="@(() => ViewDetail(context.Id))">
                    Detail
                </MudButton>
            </MudTd>
        </RowTemplate>
    </MudTable>
</MudContainer>

@code {
    private List<ListingSummaryDto> listings = new();
    private bool loading = true;
    
    protected override async Task OnInitializedAsync()
    {
        try {
            var filter = new ListingFilterDto(Page: 1, PageSize: 20);
            var result = await ListingService.SearchAsync(filter, default);
            listings = result.Items;
        } catch (Exception ex) {
            Snackbar.Add($"Failed to load listings: {ex.Message}", Severity.Error);
        } finally {
            loading = false;
        }
    }
    
    private void ViewDetail(Guid id)
    {
        Navigation.NavigateTo($"/listings/{id}");
    }
}
```

---

## Development Workflows

### Adding a New Feature

**Example: Add "Favorite Listings" feature**

1. **Database migration**
```csharp
// Add property to UserListingState entity
public bool IsFavorite { get; set; }

// Create migration
dotnet ef migrations add AddIsFavoriteToUserStates --project src/RealEstate.Infrastructure
dotnet ef database update --project src/RealEstate.Api
```

2. **Update DTO**
```csharp
public record UserListingStateDto(
    Guid ListingId,
    bool IsFavorite,
    string? Notes
);
```

3. **Add service method**
```csharp
public async Task<UserListingStateDto> ToggleFavoriteAsync(
    Guid listingId, 
    CancellationToken ct)
{
    var state = await context.UserListingStates
        .FirstOrDefaultAsync(s => s.ListingId == listingId, ct);
    
    if (state == null)
    {
        state = new UserListingState { ListingId = listingId, IsFavorite = true };
        context.UserListingStates.Add(state);
    }
    else
    {
        state.IsFavorite = !state.IsFavorite;
    }
    
    await context.SaveChangesAsync(ct);
    return new UserListingStateDto(state.ListingId, state.IsFavorite, state.Notes);
}
```

4. **Add endpoint**
```csharp
group.MapPost("/{id:guid}/favorite", ToggleFavorite)
    .WithName("ToggleFavorite");
```

5. **Update UI**
```razor
<MudToggleIconButton @bind-Toggled="@isFavorite"
                     Icon="@Icons.Material.Outlined.FavoriteBorder"
                     ToggledIcon="@Icons.Material.Filled.Favorite"
                     OnToggledChanged="HandleFavoriteToggle" />
```

### Debugging a Scraper

1. **Test HTML parsing locally**
```python
# Test script
import httpx
from bs4 import BeautifulSoup

async def test_parse():
    async with httpx.AsyncClient() as client:
        resp = await client.get("https://www.remax-czech.cz/reality/vyhledavani/")
        html = resp.text
        
        soup = BeautifulSoup(html, "html.parser")
        links = soup.select('a[href*="/reality/detail/"]')
        
        print(f"Found {len(links)} listings")
        for link in links[:5]:
            print(f"  - {link.get_text(strip=True)}: {link['href']}")

import asyncio
asyncio.run(test_parse())
```

2. **Check database insertion**
```bash
docker exec -it realestate-db psql -U postgres -d realestate_dev

SELECT 
    l.external_id, 
    l.title, 
    l.price, 
    s.name as source
FROM re_realestate.listings l
JOIN re_realestate.sources s ON l.source_id = s.id
WHERE l.created_at > NOW() - INTERVAL '1 hour'
ORDER BY l.created_at DESC
LIMIT 10;
```

3. **Monitor scraper logs**
```bash
cd scraper
python run_api.py

# In another terminal
curl -X POST http://localhost:8001/v1/scrape/run \
  -H "Content-Type: application/json" \
  -d '{"source_codes":["REMAX"],"full_rescan":false}'

# Watch logs for errors
```

---

## Common Pitfalls & Solutions

### Issue: EF Core doesn't map enum correctly
**Symptom:** `InvalidCastException` when querying listings  
**Cause:** Database has Czech values ("Prodej"), C# enum has English ("Sale")  
**Solution:** Add enum converter in `RealEstateDbContext`:
```csharp
modelBuilder.Entity<Listing>()
    .Property(l => l.OfferType)
    .HasConversion(
        v => v.ToString(),
        v => Enum.Parse<OfferType>(v)
    );
```

### Issue: MudBlazor compile error "Cannot infer type"
**Symptom:** `CS0411: The type arguments for method cannot be inferred`  
**Cause:** MudBlazor 9.x requires explicit type parameters  
**Solution:** Add `T` or `TData`:
```razor
<MudChip T="string">Source</MudChip>
<MudCarousel TData="object">...</MudCarousel>
```

### Issue: Scraper crashes on missing selectors
**Symptom:** `AttributeError: 'NoneType' object has no attribute 'get_text'`  
**Cause:** Website changed structure, selector no longer matches  
**Solution:** Use defensive parsing with optional chaining:
```python
# ❌ Bad
title = soup.find('h1').get_text()

# ✅ Good
h1 = soup.find('h1')
title = h1.get_text(strip=True) if h1 else "Unknown"
```

### Issue: Photos not syncing after upsert
**Symptom:** Old photos remain in database  
**Cause:** `_upsert_photos` not wrapped in transaction  
**Solution:**
```python
async def _upsert_photos(self, conn, listing_id, photo_urls):
    # ✅ MUST be in transaction
    async with conn.transaction():
        await conn.execute("DELETE FROM listing_photos WHERE listing_id = $1", listing_id)
        # ... INSERT new photos
```

---

## Performance Considerations

### Database Queries
```csharp
// ❌ Bad - N+1 queries
foreach (var listing in listings) {
    var photos = await context.ListingPhotos
        .Where(p => p.ListingId == listing.Id)
        .ToListAsync();
}

// ✅ Good - Single query with Include
var listings = await context.Listings
    .Include(l => l.Photos)
    .ToListAsync();
```

### Scraping
```python
# ❌ Bad - Sequential requests
for url in urls:
    html = await client.get(url)
    await parse(html)

# ✅ Good - Concurrent requests (with rate limiting)
semaphore = asyncio.Semaphore(5)  # Max 5 concurrent
tasks = [fetch_with_semaphore(url, semaphore) for url in urls]
results = await asyncio.gather(*tasks)
```

---

## Testing Checklist

Before committing new code:

- [ ] All async methods have `CancellationToken` parameter
- [ ] Error handling provides user feedback (Snackbar/logging)
- [ ] Database queries use `AsNoTracking()` for read-only
- [ ] Enum values mapped correctly (Czech ↔ English)
- [ ] MudBlazor components have explicit type parameters
- [ ] Scraper uses regex selectors (not brittle CSS classes)
- [ ] Photo sync runs in transaction
- [ ] Configuration URLs not hardcoded (use appsettings.json)

---

## Resources

- **Full session summary:** /docs/AI_SESSION_SUMMARY.md
- **Technical design:** /docs/TECHNICAL_DESIGN.md
- **Scraper documentation:** /scraper/REMAX_SCRAPER.md
- **Database schema:** /scripts/init-db.sql
- **GitHub:** https://github.com/cybersmurf/RealEstateAggregator

---

**When you make suggestions:**
1. Always consider async/await requirements
2. Check existing patterns in similar files
3. Verify configuration values (don't hardcode)
4. Include error handling and logging
5. Test database queries manually before suggesting
6. Provide full context (imports, type hints, etc.)

**Your expertise is valued in:**
- Architectural decisions (.NET + Python integration)
- Database optimization (EF Core queries, indexing)
- Scraping strategies (robustness, rate limiting)
- Full-stack debugging (tracing issues across layers)

---

**Last Updated:** 22. února 2026  
**Current State:** Production-ready with REMAX scraper. MM Reality and Prodejme.to scrapers need implementation.
