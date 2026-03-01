# GitHub Copilot Instructions – RealEstateAggregator

**Project:** Real Estate Aggregator with Semantic Search & AI Analysis  
**Stack:** .NET 10, Blazor Server, PostgreSQL 15 + **PostGIS 3.4** + pgvector, Python FastAPI scrapers, **MCP Tools for Claude Desktop**  
**Last Updated:** 1. března 2026 (Session 26)

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
| REAS | Reas.cz | reas_scraper.py |

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

### MCP Tools for AI Analysis

**Model Context Protocol** – integrace s Claude Desktop pro přímou správu analýz.

**Umístění:** `mcp/server.py` – custom MCP server běžící na `http://localhost:5001/api/*`

**Konfigurace:** `~/Library/Application Support/Claude/claude_desktop_config.json`
```json
{
  "mcpServers": {
    "realestate": {
      "command": "python3",
      "args": ["/Users/petrsramek/Projects/RealEstateAggregator/mcp/server.py"],
      "env": {
        "REALESTATE_API_URL": "http://localhost:5001"
      }
    }
  }
}
```

**Workflow pro Claude Desktop:**
```python
# 1. Najít inzeráty
search_listings(query="Znojmo dům 3M")

# 2. Načíst KOMPLETNÍ detail (+ ZÁPIS Z PROHLÍDKY!)
get_listing(listing_id="14fe1165...")
# Vrátí: cena, plocha, GPS, popis, Drive URL,
#        📋 ZÁPIS Z PROHLÍDKY (poznámky z osobní návštěvy),
#        📸 fotky z inzerátu + 📷 fotky z prohlídky

# 3. Přečíst všechny existující analýzy
get_analyses(listing_id="14fe1165...")
# Vrátí: historii všech analýz (plný obsah bez zkrácení)

# 4. Uložit NOVOU analýzu
save_analysis(
    listing_id="14fe1165...",
    content="# Analýza...",
    title="Analýza z prohlídky 26.2.2026",
    source="claude"  # automaticky tagged
)
# Analýza se uloží do DB + vygeneruje pgvector embedding → prohledávatelná přes RAG
```

**Dostupné MCP Tools:**
| Tool | Popis | Read/Write |
|------|-------|------------|
| `search_listings` | Najít inzeráty dle query, filtru | ✅ Read |
| `get_listing` | Kompletní detail + ZÁPIS Z PROHLÍDKY | ✅ Read |
| `get_analyses` | Všechny uložené analýzy (plný obsah) | ✅ Read |
| `get_inspection_photos` | Vlastní fotky z prohlídky | ✅ Read |
| `save_analysis` | Uložit novou analýzu + embedding | ✍️ Write |

**Klíčové vlastnosti:**
- `source="claude"` – každá analýza je automaticky označená zdrojem
- **Plný obsah bez zkrácení** – `get_analyses` vrací kompletní text analýz
- **ZÁPIS Z PROHLÍDKY je v get_listing** – automaticky součástí dat, ne samostatný call
- **Embedding auto-generuje** – každá uložená analýza dostane pgvector embedding pro RAG
- **Drive URL** – `get_listing` vrací přímý odkaz na Google Drive složku s exporty

⚠️ **Restart Claude Desktop** po změnách v `mcp/server.py` nebo config!

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

### ✅ Dokončeno v Session 7 (2026-02-26)
- [x] **PostGIS 3.4 + pgvector ARM64** – nativní Docker image `postgis/postgis:15-3.4` s `platform: linux/arm64/v8` (bez Rosetta emulace); migrace `scripts/migrate_postgis.sql` přidává geometrické sloupce + spatial_areas tabulku
- [x] **ČÚZK/RUIAN integrace** – `listing_cadastre_data` tabulka (`scripts/migrate_cadastre.sql`), `ruian_service.py`, 3 RUIAN endpointy na scraper API (`/v1/ruian/single`, `/v1/ruian/bulk`, `/v1/ruian/stats`), `CadastreService.cs` + `ICadastreService` (.NET), 4 endpointy `/api/cadastre/...`
- [x] **KN v detailu inzerátu** – `ListingDetail.razor`: sekce „Katastr nemovitostí", tlačítko „Otevřít v KN" (nahlížení.cuzk.cz deep link `?typeCode=adresniMisto&id={ruianKod}`), tlačítko „Najít přímý odkaz (RUIAN)"
- [x] **KN checklist v AI šablonách** – tabulka „Co ověřit v katastru nemovitostí" (zástavní práva, věcná břemena, výměra, druh pozemku, přístupová cesta) v `ai_instrukce_existing.md` i `ai_instrukce_newbuild.md`
- [x] **Bug fix: DatabaseManager.acquire()** – opraveno neexistující `get_connection()` → `async with db_manager.acquire() as conn:` v `ruian_service.py` a `main.py`
- [x] **Bug fix: spatial_areas trigger** – `CREATE OR REPLACE FUNCTION update_updated_at_column()` přidáno do `migrate_postgis.sql` (funkce chyběla při prvním běhu mimo init-db.sql)
- [x] **ARM64 collation fix** – `ALTER DATABASE realestate_dev REFRESH COLLATION VERSION` po přechodu na ARM64 postgres image
- [x] **Unit testy 111 → 141** (+30 testů): `CadastreTests.cs` – `PreferMunicipality` (10 variant přes reflection), `ListingCadastreData` defaults, `ListingCadastreDto` record equality, `BulkRuianResultDto`, `SaveCadastreDataRequest`, RUIAN URL formát (3 InlineData), `RuianFindUrl` konstanta přes reflection
### ✅ Dokončeno v Session 19 (2026-02-27)
- [x] **KN OCR screenshot** – `POST /api/cadastre/listings/{id}/ocr-screenshot` (multipart `IFormFile`); `CadastreService.OcrScreenshotAsync()` volá Ollama Vision `llama3.2-vision:11b` s podrobným KN promptem; parsuje `KnOcrData` (parcel_number, lv_number, land_area_m2, land_type, municipality, encumbrances[]); upsertuje `ListingCadastreData` s `FetchStatus="ocr"`; `CadastreOcrResultDto` vrací data + raw JSON
- [x] **kn-ocr.js** – clipboard paste (`Ctrl+V`) + drag&drop na drop-zónu → `[JSInvokable] ReceivePastedImageAsync(base64, mimeType)` v Blazor; `knOcr.init(dotNetRef, elementId)` + `knOcr.dispose()`; script tag v `App.razor`
- [x] **KN OCR UI v ListingDetail.razor** – drop-zóna `kn-ocr-dropzone` s drag feedback, `MudFileUpload` tlačítko, preview obrázku, tabulka výsledků (parcelní č., LV, výměra, druh, vlastník), seznam bretmen (`ParseEncumbrances()` List-based), Snackbar feedback, cleanup v Dispose
- [x] **bulk-classify-inspection endpoint** – `POST /api/photos/bulk-classify-inspection?batchSize=N&listingId=X` – Vision klasifikace fotografií z prohlídky (`user_listing_photos`) pro konkrétní inzerát
- [x] **Bulk-normalize progress** – background job normalizoval stávající inzeráty; stav: **249/1416** (~17 %) k datu commitu; job se dá znovu spustit: `curl -X POST "http://localhost:5001/api/ollama/bulk-normalize?batchSize=50"`
### ✅ Dokončeno v Session 8 (2026-02-27)
- [x] **APScheduler naplánovaný scraping** – `scraper/api/main.py`: `AsyncIOScheduler`, `daily_scrape` (3:00 denně) + `weekly_full_rescan` (neděle 2:00); 5 endpointů `/v1/schedule/jobs|trigger-now|pause|resume|cron`; `settings.yaml` scheduler sekce
- [x] **Fine-tuning guide** – `fine-tuning/` adresář: `README.md` (Unsloth+QLoRA+SFTTrainer workflow), `finetune_unsloth.py`, `prepare_dataset.py`, `export_to_ollama.sh`, `requirements.txt`
- [x] **REAS scraper oprava** – REAS.cz filtroval 100% výsledků na geo filtru; opraveno URL filtrem `jihomoravsky-kraj/cena-do-10-milionu` (141 domů, ~15 strán); `locality_hint="Jihomoravský kraj"` pro subobce; guard count>500 pro nefunkční segmenty; logo `REAS.svg` přidáno
- [x] **Scraper analýza** – ZNOJMOREALITY (5), DELUXREALITY (5), LEXAMO (4): ověřeno živě, scrapers fungují správně; weby jsou malé lokální realitky s omezeným portfoliem (max 7|10|8 inz. celkem)

### ✅ Dokončeno v Session 9 (2026-02-26)
- [x] **Bulk geocoding endpoint** – `POST /api/spatial/bulk-geocode?batchSize=N`; EF Core LINQ SELECT + `ExecuteSqlRawAsync` UPDATE; Nominatim 1.1s rate limit; `ExtractCityFromLocationText()` heuristika (čárka, číslo pattern); výsledek: 748/1403 inzerátů geocodováno (728 via Nominatim, 20 ze scraperu → 53% GPS pokrytí, 741 bodů na mapě)
- [x] **Prostorové filtrování kompletní** – `SpatialService.cs`: `BuildCorridorAsync` (OSRM + `ST_Buffer` EPSG:5514), `SearchInAreaAsync` (`ST_Intersects`), `GetAllMapPointsAsync`; `Map.razor`: koridor UI (start/end/buffer/OSRM toggle/save), barevné markery dle typu/nabídky, Leaflet popup (foto+cena+link), GPS coverage panel s one-click geocodingem; `leaflet-interop.js`: init/setMarkers/drawCorridor/clearCorridor/fitMarkers/destroy
- [x] **Saved areas panel** – Map.razor zobrazuje uložené koridory jako kliknutelné chipy; klik naplní formulář a znovu postaví koridor přes API; `LoadSavedAreasAsync` + `LoadSavedAreaAsync` metody
- [x] **Kontejnerizace Blazor App** – `realestate-app` Docker kontejner běží v docker-compose.yml (port 5002, healthy) → TODO splněno

### ✅ Dokončeno v Session 11 (2026-02-26)
- [x] **Python scraper unit testy (83/83)** – `scraper/tests/test_parsers.py`: ProdejmeToScraper (_parse_price 5×, _parse_area 5×, _normalize_offer_type 6×, _infer_property_type 9×), RemaxScraper mock HTML (_parse_list_page 5×, _parse_detail_page 6×), ReasScraper (_extract_ads_list 5×, _parse_description 3×, PROPERTY_TYPE_MAP 8×), ZnojmoRealityScraper (_parse_listing 4×, _extract_price_from_context 2×); `scraper/tests/test_filters.py`: FilterManager quality filtry (6×), geo filtr (5×), cenové limity House (4×) + Land (2×), kombinované (3×), default config (4×); `scraper/pytest.ini` + `scraper/tests/__init__.py`

### ✅ Dokončeno v Session 10 (2026-02-26)
- [x] **Geocoding 97%** – 7 batchí → 1366/1403 geocodováno (1346 via Nominatim, 20 ze scraperu); 32 zbývajících nelze geocódovat (špatné lokality)
- [x] **UserStatus filtr v UI** – `MudSelect "Můj stav"` v Listings.razor filtru; quick filtry ❤️ Oblíbené + 🚗 K návštěvě; `ListingsPageState` rozšíren o `UserStatus`; SessionStorage persist
- [x] **Export CSV** – `GET /api/listings/export.csv`; `IListingService.ExportCsvAsync` + `ListingService` implementace (pageSize=5000); UTF-8 BOM (Excel), semicolony, česky; MudIconButton v toolbaru; `BuildCsvExportUrl()` z `Http.BaseAddress`
- [x] **REAS full_rescan** – job úspěšný (20 inzerátů = správně, JMK lokální filtr)
- [x] **Photo download pipeline** – `PhotoDownloadService.cs` + `IPhotoDownloadService`; `POST /api/photos/bulk-download?batchSize=N` (1-200); `GET /api/photos/stats`; ukládá do `wwwroot/uploads/listings/{id}/photos/`, `stored_url` = `{PHOTOS_PUBLIC_BASE_URL}/uploads/...`; `uploads_data` Docker volume; ~365ms/fotka; 53/53 testovací batche ✓
- [x] **Photo download UI panel** – Map.razor: panel pod GPS (celkem/staženo/%), tlačítko `Stáhnout 100 fotek` → bulk-download batch; `LoadPhotoStatsAsync()` v `OnAfterRenderAsync`

### High Priority (zbývá)
- [x] Photo download pipeline – original_url → stored_url (S3/local) ✅ (local uploads_data volume + PhotoDownloadService)
- [x] Kontejnerizace Blazor App – `realestate-app` Docker kontejner hotov ✅
- [x] Prostorové filtrování – `ST_Buffer` koridor (PostGIS) + Leaflet mapa + bulk geocoding ✅

### Scraper kvalita
- [x] ZNOJMOREALITY/DELUXREALITY/LEXAMO – ověřeno: nízké počty = malé lokální realitky se skutečně omezeným portfoliem, ne chyba scraperu ✅
- [x] REAS – opraveno: `jihomoravsky-kraj/cena-do-10-milionu` URL filtr + `locality_hint` pro subobce + guard (count > 500 = skip) ✅
  - Pouze `domy` (count=141) funguje s JMK lokálním filtrem + cenovým stropem
  - `pozemky/komerci/ostatni` s lokálním filtrem vrátí count=5124 (= celá ČR) – odebráno z CATEGORIES
  - `MAX_EXPECTED_CATEGORY_COUNT = 500` guard v `_scrape_category` chrání před přetížením
  - Logo `REAS.svg` staženo a přidáno do Listings.razor + Home.razor
- [ ] Playwright fallback – pro JS-heavy weby

### Medium Priority
- [x] Semantic search – RAG service s pgvector (Ollama `nomic-embed-text` 768D, OpenAI 1536D), `FindSimilarAsync` přes `embedding <->` L2 distance ✅
- [x] Analysis jobs – `AnalysisService` + `RagService.SaveAnalysisAsync` + `BulkEmbedDescriptionsAsync` ✅
- [ ] User listing states – uložit/archivovat/kontakt tracking (základ hotov, rozšíření zbývá)
- [x] **Moje inzeráty** – `GET /api/listings/my-listings` + `MyListings.razor` (/my-listings): karty seskupené dle stavu (K návštěvě/Zajímavé/Navštíveno/Nezajímavé), souhrn čipů s počty, prázdný stav, ikona poznámek, NavMenu entry ✅
- [x] Background scheduled scraping – APScheduler `AsyncIOScheduler` v `scraper/api/main.py`, cron 3:00 denně + neděle 2:00 ✅

### Low Priority
- [x] **Unit testy scraper** – pytest 83/83 zelených; `scraper/tests/test_parsers.py` (ProdejmeToScraper, RemaxScraper, ReasScraper, ZnojmoRealityScraper) + `scraper/tests/test_filters.py` (FilterManager geo/quality/price); `scraper/pytest.ini` ✅
- [x] **Monitoring – Serilog structured logging** – `Serilog.AspNetCore` 9 + `CompactJsonFormatter` + Enrichers (Environment/Process/Thread); bootstrap logger; `UseSerilogRequestLogging()` (HTTP metoda/path/status/čas); `appsettings.json` MinimumLevel overrides ✅
- [x] Export funkce (CSV/Excel) – CSV export implementován v Session 10 (`GET /api/listings/export.csv`, UTF-8 BOM, semicolony) ✅
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

**Problem:** Python scraper volá `db_manager.get_connection()` → `AttributeError`  
**Solution:** DatabaseManager nemá metodu `get_connection()`. Správný pattern: `async with db_manager.acquire() as conn:`. Pro READ a WRITE operace v jedné funkci použij dvě oddělené `acquire()` volání.

**Problem:** `CREATE TRIGGER` selže s `function update_updated_at_column() does not exist`  
**Solution:** Funkce je definována v `init-db.sql` (běží jen na čistém DB). Pro migrace existující DB musí `migrate_postgis.sql` obsahovat `CREATE OR REPLACE FUNCTION update_updated_at_column()` před triggerem.

**Problem:** Postgres Docker image hlásí collation version mismatch po přechodu na ARM64 image  
**Solution:** `docker exec -it realestate-db psql -U postgres -d realestate_dev -c "ALTER DATABASE realestate_dev REFRESH COLLATION VERSION;"`

**Problem:** `platform: linux/arm64/v8` chybí v docker-compose.yml – Rosetta AMD64 emulace  
**Solution:** `postgis/postgis:15-3.4` bez platform spec stáhne AMD64 variantu a běží přes Rosetta 2. Přidej `platform: linux/arm64/v8` do postgres service v docker-compose.yml + `docker compose pull postgres && docker compose up -d --no-deps postgres`.

---

## Resources

- **Repository:** https://github.com/cybersmurf/RealEstateAggregator
- **Session Summary:** /docs/AI_SESSION_SUMMARY.md
- **Technical Design:** /docs/TECHNICAL_DESIGN.md
- **API Contracts:** /docs/API_CONTRACTS.md
- **Backlog:** /docs/BACKLOG.md
- **Database Schema:** /scripts/init-db.sql
- **PostGIS migrace:** /scripts/migrate_postgis.sql
- **Katastr migrace:** /scripts/migrate_cadastre.sql
- **Loga zdrojů:** /src/RealEstate.App/wwwroot/images/logos/ (13 souborů SVG/PNG)

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

### ✅ Dokončeno v Session 13 (2026-02-26)
- [x] **Serilog structured logging** – `Serilog.AspNetCore` 9 + `CompactJsonFormatter` + Enrichers (Environment/Process/Thread); bootstrap logger pro zachycení chyb před DI; `UseSerilog` s `ReadFrom.Configuration` + `ReadFrom.Services`; Development: obarvenÿ console output s SourceContext; Production: CompactJsonFormatter (JSON) pro log aggregaci; `UseSerilogRequestLogging()` – HTTP metoda, cesta, status, čas obsluhy; `appsettings.json` MinimumLevel overrides (EF Core/Microsoft → Warning); `try/catch/finally` wrapper s `Log.Fatal` + `Log.CloseAndFlush()`

### ✅ Dokončeno v Session 20 (2026-02-27)
- [x] **ARCHITECTURE.md kompletní přepis** – 53 205 znaků, 16 sekcí, Mermaid diagramy všude (Docker Compose architektura, scraping flow, FilterManager, APScheduler, RAG ingestion/retrieval/generation + plný sekvenční diagram, KN OCR end-to-end, koridor PostGIS pipeline, geocoding pipeline, robustní JSON parsování); RAG matematika (cosine similarity vzorec), EPSG:5514 vysvětlení, ERD se všemi tabulkami, indexová strategie tabulkou, embedding batch sizes
- [x] **bulk-download `?onlyMyListings=true`** – `PhotoDownloadService.DownloadBatchAsync()` + `IPhotoDownloadService` interface + `PhotoEndpoints` rozšířeny o filtr `Liked/ToVisit/Visited` přes `EXISTS` subquery na `user_listing_states`; šetří disk (nestahuje 15k fotek pro nezajímavé inzeráty)

### ✅ Dokončeno v Session 23 (2026-02-27)
- [x] **REAS CDN pagination bug** – CDN cachuje HTML `?page=N` stránky (vždy page 1 = 10 inzerátů); fix: 2. CATEGORIES entry `?sort=newest` → ~18–20 unikátních inzerátů/run; `full_rescan=True` → `_next/data/{buildId}` API (bypass CDN, reálná paginace), `_get_build_id()`, `_fetch_listing_page_api()` s GPS bbox post-filtrem, `seen_ids` dedup
- [x] **REAS anonymized listingy** – skip `isAnonymized/isAnonymous=True` inzerátů (REAS subscription-only, municipality count=0, nelze sesbírat); Kuchařovice REAS `69a188220233fdb43521d123` = záměrně skryté
- [x] **Commit** `e01d725` – fix(reas): fix CDN pagination + add sort=newest category

### ✅ Dokončeno v Session 25 (2026-02-28)
- [x] **AI coverage analýza** – potvrzeno: 91 % normalize/smart-tags a 79 % price-signal = **100 % zpracovatelných dat**; zbývající inzeráty záměrně přeskočeny (popis < 100 znaků → 132 inz., cena NULL → 327 inz.)
- [x] **Full rescan všech 13 scraperů** – zachycení zameškáných inzerátů po SReality geo filter fixu (commit `91b8157`)
- [x] **AI joby doběhly** – 1558 celkem, 1425 smart-tags, 1422 normalize, 1226 price-signal

### ✅ Dokončeno v Session 24 (2026-02-27)
- [x] **SReality geo filtr fix** – bug: API vrací pro malé obce `"Ulice, Obec"` bez okresu (napr. `"Ke Kapličce, Kuchařovice"`) → geo filtr zahodil listing; fix v `filters.py`: `combined_location` nyní kombinuje `location_text + district + municipality + region`; fix v `sreality_scraper.py`: `DISTRICT_ID_TO_NAME` mapping + `_normalize_list_item` naplní `district='Znojmo'` pro `locality_district_id=77`
- [x] **Listing 2031444812** (Ke Kapličce, Kuchařovice, 4,39M) ihned sesbírán po fixu; full_rescan SREALITY pro dočerpání dalších zameškáných inzerátů
- [x] **Commit** `91b8157` – fix(sreality): geo filter miss pro obce bez okresu v location_text

### ✅ Dokončeno v Session 26 (2026-03-01)
- [x] **Photo lightbox** – kliknutí na fotku v AI klasifikaci otevírá fullscreen overlay s ‹/› navigací, počítadlem N/M, Escape/ArrowLeft/ArrowRight/backdrop-close, kategorie + confidence dole; `_lbOpen`/`_lbIdx` state + 5 metod in `ListingDetail.razor`
- [x] **Drive analýzy diacritic fix** – `name contains 'analyz'` v GD API je case-insensitive ale diacritic-sensitive (ý ≠ y); fix: fetch all files + C# filter pro `analyz` i `analýz` variant; `Analyza_Kucharovice.pdf`/`.docx` nyní viditelné
- [x] **RAG_UI_DESIGN.md** – nový soubor `docs/RAG_UI_DESIGN.md` (1384 řádků, 16 sekcí): obecné UI standardy pro embedded+standalone RAG chat s MudBlazor 9 (bubbles, citation cards, KB management, streaming, WCAG 2.2 AA, state management)
- [x] **Colima secrets fix** – bind mount `./secrets:/app/secrets` nefunguje na Colima → `/app/secrets/` bylo prázdné → Drive credentials chyběly → analýzy se nezobrazovaly; fix: `make secrets-sync` (docker cp) volaný z `make up` + `make rebuild-api`
- [x] **Debugging postup** – identifikace přes API log `FileNotFoundException: '/app/secrets/google-drive-sa.json'`, `docker inspect` ověřil mount, `docker exec ls` ukázal prázdný dir

**Last Updated:** 1. března 2026 (Session 26)
**Current Commit:** `9136d58` – fix(secrets): add secrets-sync make target for Colima bind mount workaround
**DB stav:** 1558 inzerátů, 13 zdrojů, GPS: 97 % pokrytí, AI: normalize 91 %, smart-tags 91 %, price-signal 79 % (= 100 % zpracovatelných dat)
**Docker stack:** plně funkční, Blazor App :5002, API :5001, Scraper :8001, Postgres :5432 (PostGIS 3.4 + pgvector ARM64 nativní), **MCP Server (Claude Desktop integration)**
**Unit testy:** 141 C# zelených + 83 Python zelených

### ✅ Dokončeno v Session 14 (2026-02-26)
- [x] **MCP Tools vylepšení** – `get_listing` docstring: zdůrazní workflow (1. load data, 2. read analyses, 3. save new); kompletní popis všech polí (ZÁPIS Z PROHLÍDKY, Drive URL, fotky); `get_analyses` docstring: zdůrazní že vrací VŠECHNY analýzy v historii (plný obsah bez zkrácení); `save_analysis` docstring: workflow uložení + auto `source="claude"`; zvýšen upload limit na 150 fotek (Kestrel 1GB, FormOptions 1GB, MudFileUpload MaximumFileCount=150); local inspection photo storage + API endpoint `GET /api/listings/{id}/inspection-photos`; `ListingDetailDto`: přidány `DriveFolderUrl`, `DriveInspectionFolderUrl`, `HasOneDriveExport`; Google Drive MCP credentials setup (`~/.gdrive-server-credentials.json`)

### ✅ Dokončeno v Session 12 (2026-02-26)
- [x] **Moje inzeráty stránka** – `MyListingsSummaryDto` + `UserListingsGroupDto`; `IListingService.GetMyListingsAsync()` + implementace; `GET /api/listings/my-listings` (inzeráty = status ≠ New, seskupené dle stavu, pořadí ToVisit→Liked→Visited→Disliked); `MyListings.razor` (barevné sekce, souhrné čipy, prázdný stav, ikona poznámek, CancellationToken); NavMenu odkaz "Moje inzeráty"; 141/141 testů zelených
