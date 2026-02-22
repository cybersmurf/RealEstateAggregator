# Gemini AI Instructions – RealEstateAggregator

**Project:** Czech Real Estate Aggregator  
**Stack:** .NET 10 + Blazor + PostgreSQL + Python scrapers  
**Documentation Style:** Concise with code examples

---

## Quick Reference

### Persistent Rules (Always Apply)

- MudBlazor 9 je primarni UI stack. Nezminuj MudBlazor 7, pokud nejde o historickou poznamku.
- Udrzuj verze stacku konzistentni napric README, QUICK_START, TECHNICAL_DESIGN, API_CONTRACTS, AI_SESSION_SUMMARY.
- Pri zmenach scrapingu aktualizuj souvisejici dokumentaci a instrukce, aby odpovidaly realnemu chovani.

```yaml
Architecture:
    Frontend: Blazor Server (:5002) + MudBlazor 9.x
  Backend: .NET 10 Minimal APIs (:5001)
  Database: PostgreSQL 15 + pgvector (:5432)
  Scraper: Python FastAPI (:8001) + asyncpg

Key Technologies:
  - EF Core 10 with snake_case naming
  - asyncpg for Python → PostgreSQL
  - httpx + BeautifulSoup for scraping
  - MudBlazor for UI components

File Structure:
  src/RealEstate.Api/        # .NET backend
  src/RealEstate.App/        # Blazor frontend
  src/RealEstate.Domain/     # Entities + enums
  scraper/core/scrapers/     # Python scrapers
  scraper/core/database.py   # asyncpg manager
```

---

## Code Patterns (Quick Copy-Paste)

### C# Minimal API Endpoint
```csharp
// src/RealEstate.Api/Endpoints/ListingEndpoints.cs
group.MapPost("/search", SearchListings).WithName("SearchListings");

private static async Task<Ok<PagedResultDto<ListingSummaryDto>>> SearchListings(
    [FromBody] ListingFilterDto filter,
    [FromServices] IListingService service,
    CancellationToken ct)
{
    var result = await service.SearchAsync(filter, ct);
    return TypedResults.Ok(result);
}
```

### Python Scraper Method
```python
# scraper/core/scrapers/example_scraper.py
async def run(self, full_rescan: bool = False) -> int:
    """Main entry point called by runner."""
    max_pages = 100 if full_rescan else 5
    return await self.scrape(max_pages)

async def scrape(self, max_pages: int) -> int:
    """Scrape listings."""
    scraped_count = 0
    
    async with httpx.AsyncClient(timeout=30) as client:
        for page in range(1, max_pages + 1):
            html = await self._fetch_page(client, page)
            items = self._parse_list_page(html)
            
            for item in items:
                detail_html = await self._fetch_page(client, item["url"])
                listing = self._parse_detail_page(detail_html, item)
                
                db = get_db_manager()
                await db.upsert_listing(listing)
                scraped_count += 1
            
            await asyncio.sleep(1)  # Rate limiting
    
    return scraped_count
```

### Database Upsert (Python)
```python
# scraper/core/database.py usage
db_manager = get_db_manager()

listing_data = {
    "source_code": "REMAX",
    "external_id": "12345",
    "title": "Prodej bytu 3+1",
    "price": 5500000.0,
    "property_type": "Byt",      # Czech
    "offer_type": "Prodej",       # Czech
    "photos": ["http://...", ...],
}

listing_id = await db_manager.upsert_listing(listing_data)
# → Converts "Byt" → "Apartment", "Prodej" → "Sale" automatically
```

### Blazor Component with MudBlazor
```razor
@page "/listings"
@inject IListingService ListingService
@inject NavigationManager Navigation
@inject ISnackbar Snackbar

<MudTable T="ListingSummaryDto" Items="@items" Loading="@loading">
    <HeaderContent>
        <MudTh>Title</MudTh>
        <MudTh>Price</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.Title</MudTd>
        <MudTd>@context.Price?.ToString("N0") Kč</MudTd>
    </RowTemplate>
</MudTable>

@code {
    private List<ListingSummaryDto> items = new();
    private bool loading = true;
    
    protected override async Task OnInitializedAsync()
    {
        try {
            var result = await ListingService.SearchAsync(new(), default);
            items = result.Items;
        } catch (Exception ex) {
            Snackbar.Add($"Error: {ex.Message}", Severity.Error);
        } finally {
            loading = false;
        }
    }
}
```

---

## Important Rules

### 1. Always Async for I/O
```csharp
// ✅ Good
public async Task<Listing?> GetByIdAsync(Guid id, CancellationToken ct)
{
    return await context.Listings.FirstOrDefaultAsync(l => l.Id == id, ct);
}

// ❌ Bad
public Listing? GetById(Guid id)
{
    return context.Listings.FirstOrDefault(l => l.Id == id);
}
```

### 2. Enum Mapping Czech ↔ English
```python
# Database stores: "House", "Apartment", "Sale", "Rent"
# Scraper sends: "Dům", "Byt", "Prodej", "Pronájem"

property_type_map = {
    "Dům": "House",
    "Byt": "Apartment",
    "Pozemek": "Land",
}

# In upsert_listing:
property_type_db = property_type_map.get(listing_data["property_type"], "Other")
```

### 3. MudBlazor Type Parameters
```razor
@* MudBlazor 9.x requires explicit types *@
<MudChip T="string">@source.Name</MudChip>
<MudCarousel TData="object">
    @foreach (var photo in photos) {
        <MudCarouselItem>
            <img src="@photo.Url" />
        </MudCarouselItem>
    }
</MudCarousel>
```

### 4. Error Handling Pattern
```csharp
// C# - User-facing errors
try {
    await operation();
    Snackbar.Add("Success", Severity.Success);
} catch (Exception ex) {
    logger.LogError(ex, "Operation failed");
    Snackbar.Add($"Error: {ex.Message}", Severity.Error);
}
```

```python
# Python - Log and re-raise
try:
    result = await db.upsert_listing(data)
    logger.info(f"Saved {result}")
except Exception as exc:
    logger.error(f"Failed: {exc}", exc_info=True)
    raise
```

### 5. Transaction for Multi-Step DB Ops
```python
# ❌ Bad - DELETE without transaction
await conn.execute("DELETE FROM photos WHERE listing_id = $1", id)
await conn.execute("INSERT INTO photos ...")  # ← Might fail, photos lost!

# ✅ Good - Atomic DELETE + INSERT
async with conn.transaction():
    await conn.execute("DELETE FROM photos WHERE listing_id = $1", id)
    await conn.execute("INSERT INTO photos ...")
```

---

## Common Tasks

### Add New Scraper
1. Copy `remax_scraper.py` → `newscraper_scraper.py`
2. Update `BASE_URL`, `SOURCE_CODE`, selectors
3. Add to `runner.py`:
```python
if "NEWSCRAPER" in source_codes:
    scraper = NewScraperScraper()
    count = await scraper.run(full_rescan)
```
4. Seed database:
```sql
INSERT INTO re_realestate.sources (id, code, name, base_url, is_active)
VALUES (gen_random_uuid(), 'NEWSCRAPER', 'New Scraper', 'https://...', true);
```

### Add API Endpoint
1. Create DTO in `Contracts/`
2. Add to `Endpoints/`:
```csharp
group.MapGet("/top", GetTopListings).WithName("GetTopListings");
```
3. Implement in Service:
```csharp
public async Task<List<ListingSummaryDto>> GetTopListingsAsync(int count, CancellationToken ct)
{
    return await context.Listings
        .OrderByDescending(l => l.Price)
        .Take(count)
        .Select(l => new ListingSummaryDto(...))
        .ToListAsync(ct);
}
```

### Add UI Page
1. Create `Pages/NewPage.razor`
2. Add route: `@page "/new-page"`
3. Add to NavMenu:
```razor
<MudNavLink Href="/new-page" Icon="@Icons.Material.Filled.Star">
    New Page
</MudNavLink>
```

---

## Configuration

### Local Development
```bash
# Start database
docker-compose up -d postgres

# Start .NET API
cd src/RealEstate.Api
dotnet run --urls "http://localhost:5001"

# Start Blazor UI
cd src/RealEstate.App
dotnet run --urls "http://localhost:5002"

# Start Python scraper
cd scraper
python run_api.py  # → http://localhost:8001
```

### Connection Strings
```json
// appsettings.Development.json
{
  "ConnectionStrings": {
    "RealEstate": "Host=localhost;Port=5432;Database=realestate_dev;Username=postgres;Password=dev"
  },
  "ScraperApi": {
    "BaseUrl": "http://localhost:8001"
  }
}
```

### Docker URLs
```yaml
# When services run in Docker:
ConnectionStrings__RealEstate: "Host=postgres;Port=5432;..."
ScraperApi__BaseUrl: "http://scraper:8001"
```

---

## Debugging

### Check Database
```bash
docker exec -it realestate-db psql -U postgres -d realestate_dev

# Count listings
SELECT COUNT(*) FROM re_realestate.listings;

# Check sources
SELECT code, name, is_active FROM re_realestate.sources;

# Recent listings
SELECT title, price, source_name FROM re_realestate.listings 
ORDER BY first_seen_at DESC LIMIT 10;
```

### Test Scraper
```bash
# Direct Python API call
curl -X POST http://localhost:8001/v1/scrape/run \
  -H "Content-Type: application/json" \
  -d '{"source_codes":["REMAX"],"full_rescan":false}'

# Via .NET API
curl -X POST http://localhost:5001/api/scraping/trigger \
  -H "Content-Type: application/json" \
  -d '{"sourceCodes":["REMAX"],"fullRescan":false}'
```

### Common Errors

| Error | Cause | Fix |
|-------|-------|-----|
| `500 from /api/scraping/trigger` | Python scraper not running | `cd scraper && python run_api.py` |
| `InvalidCastException` on enum | Czech values in DB | Add enum converter in DbContext |
| `CS0411` MudBlazor type error | Missing type parameter | Add `T="string"` or `TData="object"` |
| `NoneType has no attribute` | Selector not found | Use defensive: `if elem: ...` |
| Photos not updating | Not in transaction | Wrap in `async with conn.transaction():` |

---

## Testing Workflow

**Before commit:**
1. Run .NET tests: `dotnet test`
2. Check Python syntax: `python -m py_compile scraper/core/scrapers/*.py`
3. Verify scraper: Test on live website, check DB insertion
4. Test UI: Navigate all pages, check Snackbar feedback
5. Review logs: No errors in console/terminal

**Example test commands:**
```bash
# Test API endpoint
curl http://localhost:5001/api/sources | jq

# Test listing search
curl -X POST http://localhost:5001/api/listings/search \
  -H "Content-Type: application/json" \
  -d '{"page":1,"pageSize":5}' | jq .items[0]

# Check scraper parses correctly
cd scraper
python -c "
from core.scrapers.remax_scraper import RemaxScraper
import asyncio
scraper = RemaxScraper()
asyncio.run(scraper.scrape(max_pages=1))
"
```

---

## Performance Tips

### EF Core
```csharp
// ❌ Slow - Tracking entities unnecessarily
var listings = await context.Listings.ToListAsync();

// ✅ Fast - No tracking for read-only
var listings = await context.Listings.AsNoTracking().ToListAsync();

// ❌ N+1 problem
foreach (var listing in listings) {
    var photos = await context.ListingPhotos
        .Where(p => p.ListingId == listing.Id)
        .ToListAsync();
}

// ✅ Single query
var listings = await context.Listings
    .Include(l => l.Photos)
    .ToListAsync();
```

### Python Scraping
```python
# ❌ Sequential
for url in urls:
    html = await client.get(url)

# ✅ Concurrent (with semaphore for rate limiting)
async def fetch_with_limit(url, sem):
    async with sem:
        return await client.get(url)

sem = asyncio.Semaphore(5)  # Max 5 concurrent
tasks = [fetch_with_limit(url, sem) for url in urls]
results = await asyncio.gather(*tasks)
```

---

## Resources

- **Session Summary:** `docs/AI_SESSION_SUMMARY.md`
- **Technical Design:** `docs/TECHNICAL_DESIGN.md`
- **Scraper Docs:** `scraper/REMAX_SCRAPER.md`
- **Database Schema:** `scripts/init-db.sql`
- **GitHub:** https://github.com/cybersmurf/RealEstateAggregator

---

## Your Role

When assisting with this project:

1. **Suggest code** - Provide complete, runnable snippets
2. **Check existing patterns** - Match style from similar files
3. **Validate assumptions** - Ask about unclear requirements
4. **Explain tradeoffs** - Why one approach over another
5. **Test before recommending** - Verify SQL queries, regex patterns

**Focus areas:**
- Multi-language integration (.NET ↔ Python)
- Database optimization (pgvector, indexing)
- Scraping robustness (regex selectors, error handling)
- UI/UX with MudBlazor components

---

**Last Updated:** 22. února 2026  
**Current Status:** REMAX scraper functional. MM Reality + Prodejme.to need implementation.
