# GitHub Copilot Instructions – RealEstateAggregator

**Project:** Real Estate Aggregator with Semantic Search & AI Analysis  
**Stack:** .NET 10, Blazor Server, PostgreSQL 15 + pgvector, Python FastAPI scrapers  
**Last Updated:** 23. února 2026 (Session 4)

---

## Project Context

Full-stack aplikace pro agregaci realitních inzerátů z českých webů. Aktuálně integruje **12 zdrojů a 1 236+ aktivních inzerátů**. Podporuje sémantické vyhledávání pomocí pgvector, filtrování dle typu nemovitosti/nabídky/ceny a AI analýzu inzerátů.

### Aktivní scrapeři (12 zdrojů)
| Kód | Název | Soubor |
|---|---|---|
| REMAX | RE/MAX Czech Republic | remax_scraper.py |
| MMR | M&M Reality | mmreality_scraper.py |
| PRODEJMETO | Prodejme.to | prodejmeto_scraper.py |
| SREALITY | Sreality.cz | sreality_scraper.py |
| IDNES | iDnes Reality | idnes_reality_scraper.py |
| CENTURY21 | CENTURY 21 Czech Republic | century21_scraper.py |
| PREMIAREALITY | Premiera Reality | premiareality_scraper.py |
| DELUXREALITY | Delux Reality | deluxreality_scraper.py |
| HVREALITY | HV Reality | hvreality_scraper.py |
| LEXAMO | Lexamo | lexamo_scraper.py |
| ZNOJMOREALITY | Znojmo Reality | znojmoreality_scraper.py |
| NEMZNOJMO | Nemovitosti Znojmo | nemovitostiznojmo_scraper.py |

## Persistent Rules (Always Apply)

- MudBlazor 9 je primarni UI stack. Nezminuj MudBlazor 7, pokud nejde o historickou poznamku.
- Udrzuj verze stacku konzistentni napric README, QUICK_START, TECHNICAL_DESIGN, API_CONTRACTS, AI_SESSION_SUMMARY.
- Pri zmenach scrapingu aktualizuj souvisejici dokumentaci a instrukce, aby odpovidaly realnemu chovani.
- **Po kazde zmene C# kodu ktera jde do Docker: `docker compose build --no-cache app api && docker compose up -d --no-deps app api`.** Zapomenuty rebuild = stary kod v kontejnerech. VZDY rebuild po zmene kodu.
- **Docker restart policy:** vsechny 4 sluzby (postgres, api, app, scraper) MUSI mit `restart: unless-stopped` v docker-compose.yml.

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

// ✅ USE: Enum converters s switch expression (NE Enum.Parse – nefunguje v EF Core expression trees)
// ⚠️ KRITICKÉ: DB ukládá anglicky ("House", "Apartment", "Sale", "Rent")
// ⚠️ NESMÍŠ použít česky ("Dům", "Byt") – způsobuje 0 výsledků při filtrování!
modelBuilder.Entity<Listing>()
    .Property(l => l.PropertyType)
    .HasConversion(
        v => v.ToString(),  // zápis: "House", "Apartment", ...
        v => v == "House" ? PropertyType.House
           : v == "Apartment" ? PropertyType.Apartment
           : v == "Land" ? PropertyType.Land
           : v == "Cottage" ? PropertyType.Cottage
           : v == "Commercial" ? PropertyType.Commercial
           : v == "Industrial" ? PropertyType.Industrial
           : v == "Garage" ? PropertyType.Garage
           : PropertyType.Other);

// OfferType analogicky:
//   v => v.ToString()  →  "Sale", "Rent", "Auction"
//   v == "Rent" ? OfferType.Rent : v == "Auction" ? OfferType.Auction : OfferType.Sale

// Table: re_realestate.listings
// Columns: id, source_id, source_code, external_id, title, property_type, offer_type, price...
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

1. **Copy remax_scraper.py template** (nebo jiný hotový scraper jako vzor)
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
        # Extract: title, price, location, photos (max 20), area
        # Mapuj property_type a offer_type pomocí property_type_map/offer_type_map z database.py
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

```python
# Database stores English. Scraper mapuje z češtiny:
property_type_map = {
    "Dům": "House", "Byt": "Apartment", "Pozemek": "Land",
    "Chata": "Cottage", "Komerční": "Commercial", "Ostatní": "Other",
}

offer_type_map = {
    "Prodej": "Sale",
    "Pronájem": "Rent",
    "Dražba": "Auction",   # ← SReality category_type_cb=3
}
```

```csharp
// OfferType enum: Sale, Rent, Auction
// DB ukládá: "Sale", "Rent", "Auction"
// HasConversion: Enum.Parse → NEPOUŽÍVAT, použij switch expression:
// v == "Rent" ? OfferType.Rent : v == "Auction" ? OfferType.Auction : OfferType.Sale
```

### SReality URL pravidla (KRITICKÉ – nerozbíjej!)

URL se builduje v `_build_detail_url()` ve `sreality_scraper.py`:
- Formát: `/detail/{cat_type_slug}/{cat_main_slug}/{cat_sub_slug}/{locality}/{hash_id}`
- `cat_type`: 1=prodej, 2=pronajem, **3=drazba**
- `_merge_detail()` VŽDY refreshuje URL z detail API SEO – nevynechávej to volání
- `_CAT_SUB_SLUG_OVERRIDES = {2: {40: "na-klic"}}` – domy na klíč mají jiný slug než SReality default
- Dražby mají krátkou životnost → URL vrátí 404 po skončení dražby. To je **expected chování**, ne bug
- Expired inzeráty jsou deaktivovány (`is_active=false`) automaticky při příštím `full_rescan` přes `deactivate_unseen_listings()`

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

### AI Analýza – template systém

Šablony pro AI instrukce jsou **runtime `.md` soubory** – editovatelné bez recompilace:

```
src/RealEstate.Api/Templates/
  ai_instrukce_existing.md   ← existující nemovitosti
  ai_instrukce_newbuild.md   ← novostavby
```

`ListingExportContentBuilder.BuildAiInstructions()` načte správnou šablonu dle `IsNewBuild()` a interpoluje `{{PLACEHOLDERS}}`:
- `{{LOCATION}}`, `{{PROPERTY_TYPE}}`, `{{OFFER_TYPE}}`, `{{PRICE}}`, `{{PRICE_NOTE}}`
- `{{AREA}}`, `{{ROOMS_LINE}}`, `{{CONSTRUCTION_TYPE_LINE}}`, `{{CONDITION_LINE}}`
- `{{SOURCE_NAME}}`, `{{SOURCE_CODE}}`, `{{URL}}`
- `{{PHOTO_LINKS_SECTION}}` – inline fotky pro AI chat
- `{{DRIVE_FOLDER_SECTION}}` – odkaz na cloud složku

`IsNewBuild()` – keywords: `novostavb`, `ve výstavb`, `pod klíč`, `developerský projekt`, `dokončení 202x`, `condition=Nový/Nová`

⚠️ Po změně šablony v Docker – `docker cp` stačí pro jednorázovou změnu, rebuild api pro trvalou:
```bash
# Jednorázová změna (do restartu):
docker cp src/RealEstate.Api/Templates/ai_instrukce_existing.md realestate-api:/app/Templates/
# Trvalá změna:
docker compose build --no-cache api && docker compose up -d --no-deps api
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

# Scraper vyžaduje virtualenv (python3 není v PATH, použij venv):
cd scraper && .venv/bin/python run_api.py
# Pokud venv neexistuje:
# python3 -m venv scraper/.venv && source scraper/.venv/bin/activate
# pip install -r scraper/requirements.txt

# Test endpoints
curl http://localhost:5001/api/sources
curl -X POST http://localhost:5001/api/listings/search \
  -H "Content-Type: application/json" \
  -d '{"page":1,"pageSize":10}'

# Trigger scraping (přes .NET API → Python Scraper API)
# ⚠️ Scraping endpointy jsou chráněny API klíčem (X-Api-Key header)
curl -X POST http://localhost:5001/api/scraping/trigger \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key-change-me" \
  -d '{"sourceCodes":["REMAX"],"fullRescan":false}'

# Nebo přímo na Python Scraper API:
curl -X POST http://localhost:8001/v1/scrape/run \
  -H "Content-Type: application/json" \
  -d '{"source_codes":["REMAX"],"full_rescan":false}'
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

### ✅ Dokončeno v Session 4 (2026-02-23)
- [x] **Security** – API key middleware na scraping endpointech (`X-Api-Key`)
- [x] **CORS** – `AddCors()` + `UseCors()` v Program.cs
- [x] **Health endpoint** – `/health` + Docker healthcheck
- [x] **Filtered Include** – UserStates N+1 odstraněno
- [x] **tsvector fulltext** – GIN index namísto ILIKE
- [x] **Retry logic** – tenacity `@http_retry` na 11 scraperech
- [x] **CancellationToken** – HTTP volání v Listings.razor s CT
- [x] **SourceDto** – extrahováno do `Models/SourceDto.cs`
- [x] **39 unit testů** – enum, NormalizeStatus, DTOs
- [x] **Tiebreaker** – `.ThenBy(x => x.Id)` pro deterministické stránkování
- [x] **user_listing_photos** tabulka v init-db.sql

### ✅ Dokončeno v Session 5 (2026-02-24)
- [x] **Docker restart policy** – `restart: unless-stopped` na všech 4 službách v docker-compose.yml
- [x] **OfferType.Auction** – přidán do enum, DbContext HasConversion, Listings.razor, ListingDetail.razor, database.py offer_type_map
- [x] **Dražba URL** – SReality `cat_type=3` → slug `drazba`; `_build_detail_url()` generuje správné URL
- [x] **deactivate_unseen_listings()** – automatická deaktivace expired inzerátů po full_rescan v runner.py
- [x] **Filter state persistence** – `ListingsPageState` + `ProtectedSessionStorage` (bylo již v Session 4 kódu, Docker image byl stary – fixed rebuildeem)
- [x] **5 expired SReality inzerátů** deaktivováno přímo v DB; 5 dražeb retroaktivně opraveno na `offer_type='Auction'`
- [x] **MSBuild CS2021 glob fix** – `EnableDefaultCompileItems=false` + explicitní Compile items v `Infrastructure.csproj`, `Api.csproj`, `Background.csproj`; Docker image api/scraper/app úspěšně rebuild

### ✅ Dokončeno v Session 6 (2026-02-25)
- [x] **AI šablony externalizovány** – `BuildAiInstructions()` načítá `.md` soubory z `src/RealEstate.Api/Templates/` místo hardcoded stringů; editovatelné bez recompilace
- [x] **GoogleDriveExportService.cs** – odstraněno ~400 řádků dead code (privátní kopie BuildAiInstructions, BuildInfoMarkdown, BuildDataJson, IsNewBuild, SanitizeName); vše přesunuto do sdíleného `ListingExportContentBuilder`
- [x] **ai_instrukce_existing.md** – kompletní přepis: tabulky (💰 finanční kalkulace, 🔧 technický stav, 📊 yield), emoji hierarchie (🔴🟡🟢✅⚠️), sekce „Co bylo renovováno", emoji VERDIKT, prohlídka TABLE
- [x] **ai_instrukce_newbuild.md** – kompletní přepis: sekce „Klíčové technologie a vybavení" místo renovace, tabulka technologií (TČ, rekuperace, smart home), NEPIŠ o rekonstrukci
- [x] **Unit testy 39 → 111** (+72 testů): `ExportBuilderTests.cs` (IsNewBuild 14 variant, SanitizeName, BuildDataJson, PhotoLinks, PageGuard), `RagServiceTests.cs` (CosineSimilarity 8 variant, BuildListingText 11 variant), `UnitTest1.cs` (+Auction enum, +Auction jako invalid user status)

### High Priority (zbývá)
- [ ] Photo download pipeline – original_url → stored_url (S3/local)
- [ ] CENTURY21 logo – placeholder SVG (274B), reálné logo za WP loginem
- [ ] Kontejnerizace Blazor App – přidat do docker-compose nebo přejít na .NET Aspire

### Scraper kvalita (zdroje s málo výsledky)
- [ ] ZNOJMOREALITY (5 inz.), DELUXREALITY (5), PRODEJMETO (4), LEXAMO (4) – ověřit selektory
- [ ] Playwright fallback – pro JS-heavy weby

### Medium Priority
- [x] Semantic search – RAG service s pgvector (Ollama `nomic-embed-text` 768D, OpenAI 1536D), `FindSimilarAsync` přes `embedding <->` L2 distance ✅
- [x] Analysis jobs – `AnalysisService` + `RagService.SaveAnalysisAsync` + `BulkEmbedDescriptionsAsync` ✅
- [ ] User listing states – uložit/archivovat/kontakt tracking (základ hotov, rozšíření zbývá)
- [ ] Background scheduled scraping – pravidelný re-run (APScheduler/Hangfire)

### Low Priority
- [ ] Unit testy – scraper parsing s mock HTML
- [ ] Monitoring – Prometheus/Serilog metrics
- [ ] Export funkce (CSV/Excel) – projekt RealEstate.Export existuje
- [ ] AI šablony – úprava sekcí dle uživatelského feedbacku z reálných analýz

---

## Debugging Tips

### Common Issues

**Problem:** API container crashes with `Failed to connect to 127.0.0.1:5432`  
**Solution:** `Program.cs` sestavuje connection string z `DB_HOST` (default `localhost`). Docker-compose MUSÍ nastavovat `DB_HOST=postgres` (ne `ConnectionStrings__RealEstate`). Zkontroluj sekci `environment:` v `docker-compose.yml`.

**Problem:** Fotky se nezobrazují v Blazor App  
**Solution:** Zkontroluj `ListingService.cs` – `StoredUrl = p.StoredUrl` (nikoliv `?? string.Empty`). Prázdný string `""` se v Blazor `photo.StoredUrl ?? photo.OriginalUrl` nezobrazí jako fallback.

**Problem:** Sources filter (chipy) – NullReferenceException při odznačení  
**Solution:** `_selectedSourceCodes` musí být `IReadOnlyCollection<string>?` (nullable). MudBlazor 9 `MudChipSet @bind-SelectedValues` může nastavit `null`. Použij `_selectedSourceCodes?.Count ?? 0`.

**Problem:** API returns 401 when calling `/api/scraping/trigger`  
**Solution:** Scraping endpointy jsou chráněny API klíčem. Přidej header `X-Api-Key: dev-key-change-me` (nebo nastav env `API_KEY`).

**Problem:** API returns 500 when calling `/api/scraping/trigger`  
**Solution:** Python scraper neběží. Spusť: `cd scraper && .venv/bin/python run_api.py`  
(Pokud `python` není v PATH, musíš použít virtualenv venv)

**Problem:** EF Core mapping errors (snake_case vs PascalCase)  
**Solution:** Ensure `UseSnakeCaseNamingConvention()` is called in DbContext

**Problem:** MudBlazor compilation errors about type inference  
**Solution:** Add explicit type parameters: `<MudChip T="string">`, `<MudCarousel TData="object">`

**Problem:** Scraper can't connect to database  
**Solution:** Zkontroluj `scraper/config/settings.yaml` – host=localhost (local) nebo host=postgres (Docker)

**Problem:** Filter vrací špatná data i po rebuildu Docker image — `docker logs` neukazuje žádné search SQL  
**Solution:** `lsof -i :5001 -P -n` — pokud tam je lokálně běžící `RealEstate.Api` proces, `kill <PID>`. Lokální dotnet proces má prioritu před Colima/Docker SSH port forwardingem. Curl pak jde na starý lokální binary místo na Docker kontejner.

**Problem:** EF Core filtry (PropertyType/OfferType) vracejí 0 výsledků  
**Solution:** Zkontroluj HasConversion v RealEstateDbContext.cs – zápis musí být `v.ToString()` ("House"), NE české hodnoty ("Dům"). DB ukládá vždy anglicky.

**Problem:** EF Core CS8198 – `out` parameter in expression tree  
**Solution:** Nepoužívej `Enum.TryParse(v, out var x)` v HasConversion lambda. Použij switch expression.

**Problem:** Navigation doesn't work in Blazor  
**Solution:** Ensure `@inject NavigationManager Navigation` is present

**Problem:** HTTP volání v Blazor pokračují i po opuštění stránky  
**Solution:** `Listings.razor` implementuje `IDisposable` + `CancellationTokenSource _cts`. Každé HTTP volání dostane `_cts.Token`, `Dispose()` volá `_cts.Cancel()`. Nové stránky musí tento pattern kopírovat.

**Problem:** Fulltext hledání je pomalé (ILIKE full scan)  
**Solution:** Využíváme `search_tsv` GIN index přes `EF.Functions.PlainToTsQuery`. Shadow property `SearchTsv` (NpgsqlTsVector) musí být nakonfigurována v `RealEstateDbContext.OnModelCreating`. Nutný `Npgsql.EntityFrameworkCore.PostgreSQL` v Api.csproj.

**Problem:** UI nereflektuje změny v C# kódu i když byl kód opraven (filtry, řazení, UI pohled)  
**Solution:** Docker app/api image je starý. **Vždy** po změně C# kódu: `docker compose build --no-cache app api && docker compose up -d --no-deps app api`. Zapoměnutý rebuild = starý kód v kontejnerech.

**Problem:** Po restartu Macu / Colimy kontejnery nenaběhnou (postgres Exited, scraper ConnectionRefused)  
**Solution:** Zkontroluj `restart: unless-stopped` u všech 4 služeb v `docker-compose.yml`. Pokud chybí: `docker update --restart=unless-stopped realestate-db realestate-api realestate-app realestate-scraper`

**Problem:** SReality dražba odkaz vrací 404  
**Solution:** Dražba skončila – SReality ihned maže inzerát. URL formát je správný (cat_type=3 → `/drazba/`), jde o expected chování. Inzerát bude deaktivován při příštím `full_rescan`.

**Problem:** MSBuild error `CS2021: File name '**/*.cs'` při `docker compose build`  
**Solution:** SDK 10.0 glob cache bug na Colima (overlay2 fs). `Pgvector.EntityFrameworkCore` nebo `Microsoft.NET.Sdk.Web` emituje literální glob do CSC místo expanded file listu. Fix: přidat do každého postiženého `.csproj`:
```xml
<EnableDefaultCompileItems>false</EnableDefaultCompileItems>
<!-- nebo pro Web SDK projekt: -->
<EnableDefaultItems>false</EnableDefaultItems>
```
A explicitně vyjmenovat `<Compile Include="Subdir/*.cs" />` bez `**` rekurze. Hotovo v `Infrastructure.csproj`, `Api.csproj`, `Background.csproj`.

**Problem:** Po změně C# kódu je nutné použít `--no-cache`  
**Solution:** Použij `docker compose build --no-cache app api` (bez cache).

**Problem:** AI instrukce šablona se nezměnila i po editaci `.md` souboru v kontejneru  
**Solution:** Soubory v `/app/Templates/` jsou součástí image – `docker cp` funguje jen do restartu. Trvalá změna: `docker compose build --no-cache api && docker compose up -d --no-deps api`.

---

## Resources

- **Repository:** https://github.com/cybersmurf/RealEstateAggregator
- **Session Summary:** /docs/AI_SESSION_SUMMARY.md
- **Technical Design:** /docs/TECHNICAL_DESIGN.md
- **API Contracts:** /docs/API_CONTRACTS.md
- **Backlog:** /docs/BACKLOG.md
- **Database Schema:** /scripts/init-db.sql
- **Loga zdrojů:** /src/RealEstate.App/wwwroot/images/logos/ (11 souborů SVG/PNG)

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

**Last Updated:** 25. února 2026 (Session 6)  
**Current Commit:** session 6 – AI šablony refactor, +72 unit testů
**DB stav:** ~1 378 aktivních inzerátů, 12 zdrojů (SREALITY=880, IDNES=168, PREMIAREALITY=51, REMAX=38, …)
**Docker stack:** plně funkční, Blazor App :5002, API :5001, Scraper :8001, Postgres :5432, MCP :8002  
**Unit testy:** 111 testů zelených (`dotnet test tests/RealEstate.Tests`)
