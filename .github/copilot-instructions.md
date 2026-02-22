# GitHub Copilot Instructions – RealEstateAggregator

**Project:** Real Estate Aggregator with Semantic Search & AI Analysis  
**Stack:** .NET 10, Blazor Server, PostgreSQL 15 + pgvector, Python FastAPI scrapers  
**Last Updated:** 22. února 2026

---

## Project Context

Full-stack aplikace pro agregaci realitních inzerátů z českých webů (REMAX, M&M Reality, Prodejme.to). Podporuje sémantické vyhledávání pomocí pgvector embeddings a AI analýzu inzerátů.

## Persistent Rules (Always Apply)

- MudBlazor 9 je primarni UI stack. Nezminuj MudBlazor 7, pokud nejde o historickou poznamku.
- Udrzuj verze stacku konzistentni napric README, QUICK_START, TECHNICAL_DESIGN, API_CONTRACTS, AI_SESSION_SUMMARY.
- Pri zmenach scrapingu aktualizuj souvisejici dokumentaci a instrukce, aby odpovidaly realnemu chovani.

### Architecture

```
Blazor UI (:5002)
    ↓ HTTP
.NET API (:5001)
    ↓ EF Core + Npgsql
PostgreSQL (:5432)
    ↑ asyncpg
Python Scraper API (:8001)
    ↓ httpx + BeautifulSoup
External Real Estate Sites
```

---

## Code Style & Conventions

### C# (.NET)
```csharp
// ✅ USE: Minimal APIs with endpoint groups
app.MapGroup("/api/listings")
   .MapPost("/search", SearchListings);

// ✅ USE: Primary constructors (C# 12)
public sealed class ListingService(IListingRepository repo, ILogger<ListingService> logger)
{
    public async Task<PagedResult> SearchAsync(ListingFilter filter)
    {
        // Implementation
    }
}

// ✅ USE: Record types for DTOs
public record ListingSummaryDto(
    Guid Id,
    string Title,
    decimal? Price,
    string LocationText
);

// ❌ AVOID: Controller classes (use Minimal APIs instead)
// ❌ AVOID: AutoMapper (use manual mapping)
```

### Database
```csharp
// ✅ USE: Snake_case via EFCore.NamingConventions
options.UseSnakeCaseNamingConvention();

// ✅ USE: Enum converters for Czech↔English mapping
modelBuilder.Entity<Listing>()
    .Property(l => l.PropertyType)
    .HasConversion(
        v => v.ToString(), // House → "House"
        v => Enum.Parse<PropertyType>(v)
    );

// Table: re_realestate.listings
// Columns: id, source_id, external_id, title, property_type...
```

### Python (Scraper)
```python
# ✅ USE: Async/await everywhere
async def scrape(self, max_pages: int = 5) -> int:
    async with httpx.AsyncClient() as client:
        # Implementation

# ✅ USE: Type hints
def _parse_detail_page(self, html: str, item: Dict[str, Any]) -> Dict[str, Any]:
    # Implementation

# ✅ USE: asyncpg for database
db_manager = get_db_manager()
await db_manager.upsert_listing(listing_data)

# ❌ AVOID: Blocking I/O (use async variants)
```

### Blazor
```razor
@* ✅ USE: MudBlazor components with explicit type parameters *@
<MudChip T="string" Size="Size.Small">@item.SourceName</MudChip>
<MudCarousel TData="object" Style="height:400px;">
    @* Content *@
</MudCarousel>

@* ✅ USE: Dependency injection *@
@inject NavigationManager Navigation
@inject ISnackbar Snackbar
@inject IListingService ListingService

@* ✅ USE: Error handling with user feedback *@
try {
    await ListingService.DoSomethingAsync();
    Snackbar.Add("Success!", Severity.Success);
} catch (Exception ex) {
    Snackbar.Add($"Error: {ex.Message}", Severity.Error);
}
```

---

## Common Tasks

### Adding a New API Endpoint

1. **Create DTO in Contracts/**
```csharp
// src/RealEstate.Api/Contracts/Listings/ListingFilterDto.cs
public record ListingFilterDto(
    string? SearchQuery,
    PropertyType? PropertyType,
    int Page = 1,
    int PageSize = 20
);
```

2. **Add endpoint in Endpoints/**
```csharp
// src/RealEstate.Api/Endpoints/ListingEndpoints.cs
group.MapPost("/advanced-search", AdvancedSearch)
    .WithName("AdvancedSearch");

private static async Task<Ok<PagedResultDto<ListingSummaryDto>>> AdvancedSearch(
    [FromBody] ListingFilterDto filter,
    [FromServices] IListingService service,
    CancellationToken ct)
{
    var result = await service.AdvancedSearchAsync(filter, ct);
    return TypedResults.Ok(result);
}
```

3. **Implement in Service**
```csharp
// src/RealEstate.Api/Services/ListingService.cs
public async Task<PagedResult<ListingSummaryDto>> AdvancedSearchAsync(
    ListingFilterDto filter, 
    CancellationToken ct)
{
    // EF Core query logic
}
```

### Adding Database Migration

```bash
# Create migration
dotnet ef migrations add AddNewFeature --project src/RealEstate.Infrastructure

# Apply migration
dotnet ef database update --project src/RealEstate.Api
```

### Creating a New Scraper

1. **Copy remax_scraper.py template**
```python
# scraper/core/scrapers/novyscraper_scraper.py
class NovyScraperScraper:
    BASE_URL = "https://novyscraper.cz"
    SOURCE_CODE = "NOVYSCRAPER"
    
    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 100 if full_rescan else 5
        return await self.scrape(max_pages)
    
    def _parse_list_page(self, html: str) -> List[Dict[str, Any]]:
        # Regex-based selectors (robust)
        
    def _parse_detail_page(self, html: str, item: Dict) -> Dict[str, Any]:
        # Extract: title, price, location, photos, area
```

2. **Add to runner.py**
```python
from ..core.scrapers.novyscraper_scraper import NovyScraperScraper

if "NOVYSCRAPER" in source_codes:
    scraper = NovyScraperScraper()
    count = await scraper.run(full_rescan=request.full_rescan)
```

3. **Seed database**
```sql
INSERT INTO re_realestate.sources (id, code, name, base_url, is_active)
VALUES (gen_random_uuid(), 'NOVYSCRAPER', 'Nový Scraper', 'https://novyscraper.cz', true);
```

---

## Important Patterns

### Enum Mapping (Czech ↔ English)

```csharp
// Database stores English: "House", "Apartment", "Sale", "Rent"
// Scraper sends Czech: "Dům", "Byt", "Prodej", "Pronájem"

// In database.py:
property_type_map = {
    "Dům": "House",
    "Byt": "Apartment",
    "Pozemek": "Land",
    "Chata": "Cottage",
    "Komerční": "Commercial",
    "Ostatní": "Other",
}

offer_type_map = {
    "Prodej": "Sale",
    "Pronájem": "Rent",
}

# In RealEstateDbContext.cs:
.HasConversion(
    v => v.ToString(),
    v => Enum.Parse<PropertyType>(v)
);
```

### Upsert Pattern (Deduplication)

```python
# scraper/core/database.py
async def upsert_listing(self, listing_data: Dict[str, Any]) -> UUID:
    # Check if exists by (source_id, external_id)
    existing = await conn.fetchrow(
        "SELECT id FROM re_realestate.listings WHERE source_id = $1 AND external_id = $2",
        source_id, external_id
    )
    
    if existing:
        # UPDATE existing
        await conn.execute("UPDATE re_realestate.listings SET ... WHERE id = $1")
    else:
        # INSERT new
        listing_id = uuid4()
        await conn.execute("INSERT INTO re_realestate.listings (...) VALUES (...)")
```

### Photo Synchronization

```python
async def _upsert_photos(self, conn, listing_id: UUID, photo_urls: List[str]) -> None:
    # ⚠️ MUST run in transaction for atomicity
    async with conn.transaction():
        # Delete old photos
        await conn.execute("DELETE FROM re_realestate.listing_photos WHERE listing_id = $1")
        
        # Insert new photos
        for idx, photo_url in enumerate(photo_urls[:20]):
            await conn.execute("INSERT INTO re_realestate.listing_photos (...)")
```

---

## Configuration

### Connection Strings

**Local development:**
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

**Docker deployment:**
```json
// appsettings.json (or environment variables)
{
  "ConnectionStrings": {
    "RealEstate": "Host=postgres;Port=5432;Database=realestate_dev;..."
  },
  "ScraperApi": {
    "BaseUrl": "http://scraper:8001"
  }
}
```

### Python Config

```yaml
# scraper/config/settings.yaml
database:
  host: localhost  # or "postgres" in Docker
  port: 5432
  database: realestate_dev
  user: postgres
  password: dev
```

---

## Testing

### Manual API Testing

```bash
# Start services
docker-compose up -d postgres
dotnet run --project src/RealEstate.Api --urls "http://localhost:5001"
dotnet run --project src/RealEstate.App --urls "http://localhost:5002"
cd scraper && python run_api.py

# Test endpoints
curl http://localhost:5001/api/sources
curl -X POST http://localhost:5001/api/listings/search \
  -H "Content-Type: application/json" \
  -d '{"page":1,"pageSize":10}'

# Trigger scraping
curl -X POST http://localhost:5001/api/scraping/trigger \
  -H "Content-Type: application/json" \
  -d '{"sourceCodes":["REMAX"],"fullRescan":false}'
```

### Database Queries

```bash
# Connect to database
docker exec -it realestate-db psql -U postgres -d realestate_dev

# Check data
SELECT COUNT(*) FROM re_realestate.listings;
SELECT code, name, is_active FROM re_realestate.sources;
SELECT l.title, l.price, s.name FROM re_realestate.listings l JOIN re_realestate.sources s ON l.source_id = s.id LIMIT 5;
```

---

## Known Limitations & TODOs

### High Priority
- [ ] MM Reality scraper – implement real selectors
- [ ] Prodejme.to scraper – implement real selectors
- [ ] Photo download pipeline – original_url → stored_url (S3/local)
- [ ] Centralize DTOs – move from Listings.razor to shared project

### Medium Priority
- [ ] Semantic search – pgvector with OpenAI embeddings
- [ ] Analysis jobs – AI analysis implementation
- [ ] User listing states – save/archive/contact tracking
- [ ] Background scheduled scraping – APScheduler integration

### Low Priority
- [ ] Unit tests – scraper parsing with mock HTML
- [ ] Retry logic – exponential backoff
- [ ] Playwright fallback – for JS-heavy sites
- [ ] Monitoring – Prometheus metrics

---

## Debugging Tips

### Common Issues

**Problem:** API returns 500 when calling `/api/scraping/trigger`  
**Solution:** Python scraper not running. Start with `cd scraper && python run_api.py`

**Problem:** EF Core mapping errors (snake_case vs PascalCase)  
**Solution:** Ensure `UseSnakeCaseNamingConvention()` is called in DbContext

**Problem:** MudBlazor compilation errors about type inference  
**Solution:** Add explicit type parameters: `<MudChip T="string">`, `<MudCarousel TData="object">`

**Problem:** Scraper can't connect to database  
**Solution:** Check `settings.yaml` has correct host (localhost vs postgres in Docker)

**Problem:** Navigation doesn't work in Blazor  
**Solution:** Ensure `@inject NavigationManager Navigation` is present

---

## Resources

- **Repository:** https://github.com/cybersmurf/RealEstateAggregator
- **Documentation:** /docs/AI_SESSION_SUMMARY.md, /docs/TECHNICAL_DESIGN.md
- **Scraper Docs:** /scraper/REMAX_SCRAPER.md
- **Database Schema:** /scripts/init-db.sql

---

## Copilot-Specific Tips

When generating code:

1. **Always use async/await** for I/O operations (database, HTTP, file)
2. **Include error handling** with try/catch and user-facing messages (Snackbar in Blazor)
3. **Follow existing patterns** from similar files (e.g., remax_scraper.py template for new scrapers)
4. **Add logging** using injected ILogger<T> or Python logging
5. **Use type hints** in Python, records in C#
6. **Check configuration** in appsettings.json before hardcoding URLs
7. **Test with curl** before implementing UI

**Example prompt for Copilot Chat:**
```
Create a new scraper for sreality.cz based on remax_scraper.py.
Use regex selectors for robustness.
Extract: title, price, location, photos (max 20), property type, area.
Include upsert to database via get_db_manager().
```

---

**Last Updated:** 22. února 2026  
**Current Commit:** 091b7eb (REMAX DB persistence implemented)
