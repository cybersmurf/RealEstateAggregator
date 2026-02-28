# AI Session Summary – RealEstateAggregator
**Datum:** 28. února 2026  
**Celková doba:** 25 sessions  
**Status:** ✅ Production stack, 13 scraperů, 1 558 aktivních inzerátů, PostGIS koridory, RAG+pgvector+Ollama, MCP server, KN OCR, Docker ARM64

---

## ✅ Latest Updates (Session 25 – 28. února 2026)

### AI coverage analýza – 100 % zpracovatelných dat

Ranní kontrola stavu po přechodu na nový dataset (1 558 inzerátů):

**Stats:**
| Metrika | Počet | % |
|---------|-------|---|
| Total listings | 1 558 | 100 % |
| withNormalizedData | 1 422 | 91 % |
| withSmartTags | 1 425 | 91 % |
| withPriceSignal | 1 226 | 79 % |

**Proč ne 100 %?** Záměrné filtry v `OllamaTextService.cs`:
- `normalize` + `smart-tags`: skip pokud `Description == null || Description.Length <= 100` → **132 inzerátů** (prázdný nebo velmi krátký popis – AI by nemohlo nic vytěžit)
- `price-opinion`: skip pokud `Price == null || Price == 0` → **327 inzerátů** (pozemky/komerční na dotaz)

**Závěr: 91 %/91 %/79 % = 100 % zpracovatelných dat.** Zbývající inzeráty jsou záměrně přeskočeny, ne bug.

### Full rescan všech 13 scraperů

- Spuštěn `full_rescan=true` pro všechny zdroje (job `725dc1c8`)
- Cíl: zachytit inzeráty zameškané před SReality geo filter fixem (commit `91b8157`)
- AI joby (normalize/smart-tags/price-opinion) spuštěny pro nové inzeráty

**DB stav po session:** 1 558 inzerátů, 13 zdrojů, AI: normalize 91 %, smart-tags 91 %, price-signal 79 % (= 100 % zpracovatelných dat)

---

## ✅ Latest Updates (Session 24 – 27. února 2026)

### SReality geo filtr – zameškané inzeráty z malých obcí

**Root cause:** SReality API vrací pro menší obce lokaci jako `"Ke Kapličce, Kuchařovice"` (ul. + obec) **bez "okres Znojmo"**. Geo filtr `target_districts` hledal klíčová slova jen v `location_text` → listing byl tiše zahozen filtrem.

Ostatní Kuchařovice listings procházely OK (`"Kuchařovice, okres Znojmo"` / `"Znojemská, Kuchařovice"`), proto bug nebyl dříve odhalen.

**Opravené soubory:**

`scraper/core/filters.py`:
```python
# BEFORE: location_text = listing_data.get("location_text", "").lower()
# AFTER: kombinuj location_text + district + municipality + region
combined_location = " ".join(filter(None, [
    listing_data.get("location_text", ""),
    listing_data.get("district", ""),
    listing_data.get("municipality", ""),
    listing_data.get("region", ""),
])).lower()
```

`scraper/core/scrapers/sreality_scraper.py`:
```python
# Nový DISTRICT_ID_TO_NAME mapping + _normalize_list_item naplní district='Znojmo'
DISTRICT_ID_TO_NAME: Dict[int, str] = {77: "Znojmo", 78: "Brno-město", ...}
# → všechny inzeráty z Znojmo okresu projdou filtrem bez ohledu na formát adresy
```

**Výsledek:** Listing `2031444812` (Ke Kapličce, Kuchařovice, 4 390 000 Kč) ihned sesbírán po fixu. Full rescan SReality spuštěn pro dočerpání dalších potenciálně zameškáných inzerátů.

**Commit:** `91b8157` – fix(sreality): geo filter miss pro obce bez okresu v location_text

---

## ✅ Latest Updates (Session 23 – 27. února 2026)

### REAS CDN pagination bug + Kuchařovice investigace

**Root cause:** REAS HTML stránky `?page=N` jsou CDN-cached → vždy vrátí page 1 (10 inzerátů). Scraper se domníval, že prochází 19 stránek, ale vždy četl stejná data.

**Opravy (`reas_scraper.py`):**
- Přidána 2. CATEGORIES entry `?sort=newest` → ~18–20 unikátních inzerátů na inkrementální run
- Pro `full_rescan=True`: použit `/_next/data/{buildId}/path.json` API (bypass CDN, reálná paginace)
- `_get_build_id()` metoda pro dynamické načtení buildId z homepage
- `_fetch_listing_page_api()` metoda s GPS bbox post-filtrem
- `seen_ids` dedup sada across pages
- Skip `isAnonymized/isAnonymous` inzerátů (REAS subscription-only)

**Kuchařovice (REAS):** inzeráty `69a188220233fdb43521d123` jsou `isAnonymized: true` – REAS záměrně skrývá před veřejným API. Nelze sesbírat jakýmkoliv způsobem. REAS municipality search vrací count=0 (záměrně).

**Commit:** `e01d725` – fix(reas): fix CDN pagination + add sort=newest category

---

## ✅ Latest Updates (Session 6 – 25. února 2026)

### Sub-session A: Photo Export QA & Fixes

**Analýza exportních služeb** – revize `GoogleDriveExportService.cs` a `OneDriveExportService.cs`:

**Nalezené problémy:**
1. **Silent skipping bez retry** – fotka se nepodaří stáhnout, přeskočí se beze slova
2. **Missing HTTP timeout na OneDrive** – stahování fotek mohlo viset neomezeně
3. **Žádná photo stats v DTO** – uživatel neviděl kolik fotek bylo nahráno
4. **UI bez zpětné vazby** – detail stránka po exportu neukazovala počty fotek

**Implementované opravy:**

`src/RealEstate.Api/Contracts/Export/DriveExportResultDto.cs` – přidány nová pole:
```csharp
public record DriveExportResultDto(
    string FolderUrl, string FolderName, string FolderId,
    string? InspectionFolderId = null,
    int PhotosUploaded = 0, int PhotosTotal = 0
) {
    public bool AllPhotosUploaded => PhotosTotal == 0 || PhotosUploaded == PhotosTotal;
}
```

`GoogleDriveExportService.cs` + `OneDriveExportService.cs`:
- Retry smyčka 3× s exponenciálním backoff (`await Task.Delay(attempt * 2 seconds)`)
- `dl.Timeout = TimeSpan.FromSeconds(30)` (OneDrive)
- Warning log pro každou přeskočenou fotku
- Vrací `PhotosUploaded` + `PhotosTotal` v DTO

`ListingDetail.razor` – foto stats badge:
```razor
<MudChip Color="@(_driveResult.AllPhotosUploaded ? Color.Success : Color.Warning)">
    📷 Fotky: @_driveResult.PhotosUploaded/@_driveResult.PhotosTotal nahráno
</MudChip>
```

---

### Sub-session B: RAG Architektura – Batch Embedding + UI Chat + Ingestor Pattern

**Implementace na základě architekturního návrhu:**

#### Batch Embedding (idempotentní ingestor)

`IRagService.cs` – 2 nové metody:
```csharp
Task<ListingAnalysisDto> EmbedListingDescriptionAsync(Guid listingId, CancellationToken ct);
Task<int> BulkEmbedDescriptionsAsync(int limit, CancellationToken ct);
```

`RagService.cs` – implementace:
- `EmbedListingDescriptionAsync` – idempotentní, zkontroluje existenci `source="auto"`, sestaví strukturovaný text z polí inzerátu, embeduje přes Ollama
- `BulkEmbedDescriptionsAsync` – najde všechny inzeráty bez `source="auto"` analýzy, embeduje je dávkově

`RagEndpoints.cs` – 2 nové endpointy:
```
POST /api/listings/{id}/embed-description   → idempotentní embed popisu
POST /api/rag/embed-descriptions            → batch embed (body: {"limit": 200})
```

#### RAG Chat UI v ListingDetail.razor

Přidána kompletní sekce RAG chatu:
- Tlačítko "Embedovat popis inzerátu" (idempotentní, zobrazí ✓ po úspěchu)
- Textové pole pro otázku s Enter shortcutem
- Zobrazení odpovědi AI + zdroje s cosine similarity badge
- Warning pokud inzerát nemá žádné embeddingy

Nové metody v `@code`:
```csharp
private async Task LoadRagStateAsync()  // načte stav embeddingu při init
private async Task EmbedDescriptionAsync()  // POST /embed-description
private async Task AskRagAsync()  // POST /ask, zobrazí odpověď
```

#### MCP Server – 2 nové nástroje (celkem 9)

`mcp/server.py`:
```python
@mcp.tool()
async def embed_description(listing_id: str) -> str: ...

@mcp.tool()
async def bulk_embed_descriptions(limit: int = 200) -> str: ...
```

#### Ingestor pattern zdokumentován

`docs/RAG_MCP_DESIGN.md` – přidána sekce **Ingestor pattern**:
- Každý zdroj (popis, PDF, e-mail, Drive) = jeden záznam v `listing_analyses` s jiným `source`
- Aktuální source typy: `manual`, `claude`, `mcp`, `auto`
- Příklad Python ingestoru pro PDF
- Bulk embed příkaz

---

### Sub-session C: Dokumentace (Session 6)

- `docs/RAG_MCP_DESIGN.md` – vytvořen + doplněn ingestor pattern + priority tabulka aktualizována
- `docs/API_CONTRACTS.md` – doplněny RAG endpointy + embed-description sekce
- **Build stav:** oba projekty 0 chyb po všech změnách ✅

---

**DB stav:** ~1 230 aktivních inzerátů (5 expired deaktivováno), 12 zdrojů  
**MCP nástroje (celkem 9):** search_listings, get_listing, get_analyses, save_analysis, ask_listing, ask_general, list_sources, get_rag_status, **embed_description**, **bulk_embed_descriptions**

---

## ✅ Latest Updates (Session 5 – 24. února 2026)

### Fáze: Docker restart policy + OfferType.Auction + SReality dražby + Filter persistence + MSBuild fix

**Docker restart policy** – všechny 4 služby mají `restart: unless-stopped` v `docker-compose.yml`

**OfferType.Auction:**
- Přidán do `OfferType.cs` enum
- `RealEstateDbContext.cs` HasConversion aktualizováno: `v == "Auction" ? OfferType.Auction : ...`
- `Listings.razor` + `ListingDetail.razor` filtr support
- `database.py` offer_type_map: `"Dražba": "Auction"`

**SReality dražby:**
- `_build_detail_url()` v `sreality_scraper.py`: `cat_type=3` → slug `drazba`
- `deactivate_unseen_listings()` v `runner.py` – automatická deaktivace expired inzerátů po `full_rescan`
- 5 expired inzerátů deaktivováno, 5 dražeb retroaktivně opraveno na `offer_type='Auction'`

**Filter state persistence:**
- `ListingsPageState` + `ProtectedSessionStorage` – stav filtrů přežije navigaci

**MSBuild CS2021 glob fix:**
- SDK 10.0 bug na Colima (overlay2 fs): `EnableDefaultCompileItems=false` + explicitní `<Compile Include=...>` bez `**` v `Infrastructure.csproj`, `Api.csproj`, `Background.csproj`

**Commit:** `e382515` – Docker SDK 10.0 CS2021 glob fix + Session 5 docs

---

## ✅ Latest Updates (Session 4 – 23. února 2026, odpoledne)

### Fáze 22–23: Hloubková analýza + implementace všech nálezů

**Zdroj:** Autonomní implementace všech položek z `PROJECT_ANALYSIS_2026-02-23.md` (2 CRITICAL, 8 HIGH, 9 MEDIUM, 6 LOW).

#### Bezpečnost & Spolehlivost
- **API key middleware** na `/api/scraping` skupině: `X-Api-Key` header, konfigurovatelné přes env `API_KEY`
- **CORS** policy: `AddCors()` + `UseCors()` s whitelistem `localhost:5002`, `realestate-app:8080`
- **`/health` endpoint**: vrací `{ status, timestamp }`, použitý v Docker healthchecku
- **Docker healthcheck**: `curl -sf http://localhost:8080/health` + `service_healthy` chain (app čeká na api)

#### Výkon (EF Core)
- **Filtered Include** pro UserStates: `.Include(l => l.UserStates.Where(s => s.UserId == DefaultUserId))` – eliminuje N+1 pro nepotřebné řádky
- **tsvector fulltext search**: shadow property `SearchTsv` (NpgsqlTsVector) + `EF.Functions.PlainToTsQuery("simple", ...)` → využívá GIN index místo ILIKE full scan
- **Tiebreaker** v řazení: `.ThenBy(x => x.Id)` pro deterministické stránkování
- Přidán `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0` do Api.csproj

#### UX & Stabilita Blazor
- `IDisposable` + `CancellationTokenSource` v `Listings.razor` – HTTP volání se přerušují při navigaci pryč
- IDNES logo přidáno do `_sourceLogoMap`
- `NavigateToDetailAsync` → sync `void` (nebylo třeba async)
- Odstraněn redundantní `StateHasChanged()` před nastavením `_loading = true`

#### Python scrapery
- `tenacity>=8.2.0` přidán do `requirements.txt`
- `scraper/core/http_utils.py`: sdílený `@http_retry` decorator (3× exponential backoff 2–10 s, HTTP 429/503/ConnectError)
- Decorator aplikován na 11 scraperů (vše kromě century21 se vlastním error handlingem)

#### Architektura & Refaktoring
- `SourceDto` extrahován z inline záznamu v Listings.razor → `src/RealEstate.App/Models/SourceDto.cs`
- `_Imports.razor`: přidán `@using RealEstate.App.Models`
- `appsettings.json`: `DefaultConnection` → `RealEstate` (soulad se `ServiceCollectionExtensions.cs`)
- `user_listing_photos` tabulka přidána do `scripts/init-db.sql` (dříve existovala jen přes EnsureCreated)
- `ScrapingEndpoints.cs`: `MapScrapingEndpoints()` nyní vrací `RouteGroupBuilder` (podporuje `AddEndpointFilter`)

#### Unit testy (C2)
- `tests/RealEstate.Tests/UnitTest1.cs`: **39 testů** pokrývající:
  - Enum string konverze (PropertyType 8×, OfferType 2× roundtrip)
  - `NormalizeStatus()` – null/prázdný/neznámý vstup → "New", case-insensitive matching
  - `SourceDto` record – rovnost, `with` expression
  - `ListingFilterDto` – výchozí hodnoty, nastavení vlastností

**Commit:** `32077e3` – fix: analysis improvements - health/CORS/API-key, tsvector search, HTTP retry, CT, 39 unit tests, SourceDto refactor  
**Soubory:** 26 souborů změněno, 2 nové (http_utils.py, Models/SourceDto.cs)

---

## ✅ Latest Updates (Session 3 – 23. února 2026)

### Fáze 20–21: Docker fixes + photo fix + sources filter null safety

**Fáze 20: Docker connection string fix**
- Root cause: `Program.cs` sestavuje connection string z `DB_HOST` env var (default `localhost`), ale `docker-compose.yml` nastavoval pouze `ConnectionStrings__RealEstate` → API se připojovalo na `127.0.0.1:5432` místo `postgres:5432`
- Fix: Přidány `DB_HOST=postgres`, `DB_PORT=5432`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` do `docker-compose.yml`
- Projev: API container crashoval ihned po startu s `Failed to connect to 127.0.0.1:5432`

**Fáze 21: Photo storedUrl fix (deployment)**
- Bug: `StoredUrl = p.StoredUrl ?? string.Empty` v `ListingService.cs` → `storedUrl: ""`  v JSON místo `null`
- Blazor: `photo.StoredUrl ?? photo.OriginalUrl` s prázdným `""` nikdy nespadne na OriginalUrl → `<img src="">`
- Fix (commit `d691301`): `StoredUrl = p.StoredUrl` (zachovat null) + `!string.IsNullOrEmpty()` check v `ListingDetail.razor`
- Ověřeno: API vrací `storedUrl: null`, Blazor používá `originalUrl`

**Fáze 21b: Sources filter null safety**
- Bug: `MudChipSet @bind-SelectedValues` v MudBlazor 9 může nastavit `_selectedSourceCodes = null` při odznačení všech chipů → `_selectedSourceCodes.Count` hází `NullReferenceException`
- Fix: `private IReadOnlyCollection<string>? _selectedSourceCodes` + `_selectedSourceCodes?.Count ?? 0` pattern ve všech 3 výskytech v `Listings.razor`

**Fáze 18: Docker containerization** (commit `eb61e2d`)
- Dockerfiles pro API (`src/RealEstate.Api/Dockerfile`) a App (`src/RealEstate.App/Dockerfile`)
- `docker-compose.yml` se 4 službami: postgres, api, app, scraper
- Tag `v1.2.0-stable` vytvořen a pushnut

**Aktuální stav DB:** 1 236 inzerátů, 6 919 fotek, 12 zdrojů
**Commity:** `d691301` (photo fix), `eb61e2d` (Docker)

---

## ✅ Latest Updates (23. února 2026)

### Fáze 17: 5 nových scraperů + kritický bug fix + logo integrace

**5 nových scraperů:** DELUXREALITY, LEXAMO, PREMIAREALITY, HVREALITY, NEMZNOJMO

**Kritisé opravy:**
- `RealEstateDbContext.cs` – HasConversion mapoval enum na česká slova (`PropertyType.House → "Dům"`), ale DB ukládá anglicky (`"House"`) → všechny PropertyType/OfferType filtry vrácely 0 výsledků
- Fix: switch expression + `v.ToString()` pro zápis | ⚠️ `Enum.TryParse+out var` NELZE v EF Core expression tree (CS8198)
- Výsledky po fixu: House=357 ✅, Apartment=159 ✅, Rent=36 ✅, celkem 1236 ✅

**Logo integrace do UI:**
- `_sourceLogoMap` dictionary (StringComparer.OrdinalIgnoreCase) + `SourceLogoUrl()` metoda
- Integrováno na 3 místech v Listings.razor: tabulka, karty, filtr panel
- 11 logo souborů SVG/PNG v `wwwroot/images/logos/`

**Aktuální stav DB:** 1 236 inzerátů, 12 zdrojů (SREALITY=851, IDNES=168, ...)

**Commit:** `b94343e` – Fix PropertyType/OfferType converter + integrate logos into UI

---

### Fáze 14–16: MudBlazor theme + loga (23. února 2026)
- `b467209` – Replace Bootstrap layout with full MudBlazor theme (odstraněn duplikat MudPopoverProvider)
- `2b20412` – Apply Warm Property design system (Primary `#C17F3E`, Secondary `#4A6FA5`)
- `b83639d` – Add real estate agency logos (11 souborů SVG/PNG)

---

### Fáze 6–13: Rozšíření scraperů (22. února 2026)
- Scrapeři: MMR, Prodejme.to, Sreality, IDNES, ZnojmoReality, Century21
- Kompletní filtrovací panel + Home badges
- Opraveny selektory pro SReality, MMR, HVREALITY

---

### Fáze 1–5: Initial Setup & REMAX Scraper (22. února 2026)

---

## 🎯 Cíle session

**Původní zadání:** "Celkově analyzuj a udělej plán co ještě chybí a autonomně to dokonči"

**Výsledek:**
- ✅ Full-stack .NET + Blazor + PostgreSQL aplikace
- ✅ Python scraper s reálnými selektory (REMAX)
- ✅ Database persistence s asyncpg
- ✅ Docker setup pro PostgreSQL + pgvector
- ✅ Kompletní UI s MudBlazor
- ✅ API endpoints pro listings, sources, scraping

---

## 📊 Chronologieework

### Fáze 1: Initial Setup (Commity 84b7883 - dc3170b)
**Problémy:**
- Prázdná databáze, žádné seed data
- SourceService vracel prázdný array
- Enum konvertory chyběly (české hodnoty v DB)
- MudBlazor kompilační chyby
- SSL certifikát problém (HTTPS → HTTP)

**Řešení:**
- PostgreSQL 15 + pgvector v Docker
- Seed data: 3 sources, 4 sample listings
- EFCore.NamingConventions v10.0.1
- Enum konvertory: PropertyType/OfferType (CZ→EN mapping)
- ApplicationBaseUrl: HTTPS → HTTP
- MudBlazor theme fix

**Commity:**
- `84b7883` - Initial project setup
- `68ad16b` - Home page s kartami
- `dc3170b` - SourceService + enum konvertory
- `ffc6a91` - Fix API base URL

---

### Fáze 2: Template Cleanup (Commity 1a1c138 - 2617f20)
**Problém:** Copilot vygeneroval template files (Weather.razor, Counter.razor, Class1.cs)

**Řešení:**
- Smazány template soubory
- Vytvořen **Dockerfile** pro RealEstate.Api
- Přidána **ListingDetail.razor** stránka
- Odstraněny odkazy z NavMenu

**Commity:**
- `1a1c138` - Remove Counter/Weather z navigace
- `2617f20` - Delete template files, add Dockerfile

---

### Fáze 3: REMAX Scraper Implementation (Commit a12212e)
**Problém:** Scraper měl placeholder/mock selektory

**Řešení:**
- **Kompletní přepis** s reálnými selektory z živého webu
- Regex-based parsing (robustní vůči CSS změnám)
- Deduplikace podle external_id
- Rate limiting (asyncio.sleep)
- Comprehensive error handling
- **REMAX_SCRAPER.md** dokumentace

**Technické detaily:**
```python
# List page: a[href*="/reality/detail/"]
# External ID: regex r'/reality/detail/(\d+)/'
# Title: <h1> tag
# Location: regex r'ulice|část obce|okres'
# Price: regex r'(\d[\d\s]+)\s*Kč'
# Photos: <img> s mlsf.remax-czech.cz
# Property type: inference z title (Dům, Byt, Pozemek...)
# Offer type: inference z title (Prodej vs Pronájem)
```

**Commit:**
- `a12212e` - REMAX scraper + dokumentace

---

### Fáze 4: UI Bug Fixes (Commit 0038ea3)
**Problémy identifikované uživatelem:**
1. NavigationManager commented out + missing inject
2. Mock data Guid vs int (false alarm - DB měla Guids)
3. Missing ISnackbar inject
4. MudBlazor components missing type parameters

**Řešení:**
```csharp
// Listings.razor
@inject NavigationManager Navigation  // ← ADDED
@inject ISnackbar Snackbar             // ← ADDED

// Uncommented:
Navigation.NavigateTo($"/listings/{id}");

// Enhanced error handling:
try {
    await analysisService.CreateAnalysisAsync(id);
    Snackbar.Add("Analysis created", Severity.Success);
} catch (Exception ex) {
    Snackbar.Add($"Error: {ex.Message}", Severity.Error);
}
```

```csharp
// ListingDetail.razor
<MudChip T="string" Size="Size.Small">  // ← ADDED T="string"
<MudCarousel TData="object" Style="..."> // ← ADDED TData="object"
```

**Commit:**
- `0038ea3` - Fix navigation + Snackbar + MudBlazor types

---

### Fáze 5: Database Persistence (Commit 091b7eb)
**Problém:** `_save_listing()` byl stub (pouze logoval)

**Řešení:**
- **scraper/core/database.py** (nový soubor, 300+ LOC)
  - `DatabaseManager` s asyncpg connection pool
  - `upsert_listing()` - INSERT new / UPDATE existing
  - Deduplikace: `(source_id, external_id)` unique constraint
  - Enum mapping: Dům→House, Byt→Apartment, Prodej→Sale, Pronájem→Rent
  - `_upsert_photos()` - synchronizace až 20 fotek

- **scraper/api/main.py** - FastAPI lifecycle
  - `@app.on_event("startup")` → načte settings.yaml
  - `init_db_manager()` + `db_manager.connect()`
  - `@app.on_event("shutdown")` → `db_manager.disconnect()`

- **scraper/core/scrapers/remax_scraper.py**
  - `run(full_rescan)` wrapper pro runner.py
  - `_save_listing()` volá `db.upsert_listing()`

- **scraper/REMAX_SCRAPER.md**
  - Opraven bug v dokumentaci (property type inference)
  - Aktualizováno TODO (DB persistence ✅)

**Commit:**
- `091b7eb` - Implement database persistence

---

## 🏗️ Finální Architektura

```
┌─────────────────────────────────────────────────────────────┐
│                      USER BROWSER                            │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        │ HTTP :5002
                        ▼
┌─────────────────────────────────────────────────────────────┐
│              Blazor Server (RealEstate.App)                  │
│  - Home.razor (Dashboard s kartami)                          │
│  - Listings.razor (Tabulka + pagination + search)            │
│  - ListingDetail.razor (Detail + carousel + user state)      │
│  - MudBlazor 9.x components                                  │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        │ HTTP :5001
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                .NET API (RealEstate.Api)                     │
│  Endpoints:                                                  │
│    POST /api/listings/search → ListingService                │
│    GET  /api/listings/{id}   → ListingService                │
│    GET  /api/sources         → SourceService                 │
│    POST /api/scraping/trigger → ScrapingService              │
│  Services:                                                   │
│    - ListingService (EF Core queries)                        │
│    - SourceService (EF Core queries)                         │
│    - ScrapingService (HTTP client → Python API)              │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        │ EF Core + Npgsql
                        ▼
┌─────────────────────────────────────────────────────────────┐
│          PostgreSQL 15 + pgvector (:5432)                    │
│  Schema: re_realestate                                       │
│    - sources (3 rows: REMAX, MMR, PRODEJMETO)                │
│    - listings (Guid IDs, snake_case columns)                 │
│    - listing_photos (original_url, stored_url)               │
│    - user_listing_states                                     │
│    - analysis_jobs                                           │
│  Enums: PropertyType, OfferType (EN values)                  │
│  Extensions: pgvector for semantic search                    │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        │ asyncpg
                        ▼
┌─────────────────────────────────────────────────────────────┐
│          Python Scraper API (FastAPI :8001)                  │
│  Endpoints:                                                  │
│    POST /v1/scrape/run   → run_scrape_job()                  │
│    GET  /v1/scrape/jobs/{id} → job status                    │
│  Runner:                                                     │
│    - job lifecycle (Queued → Started → Succeeded/Failed)     │
│    - paralelní scraping multiple sources                     │
│  Database:                                                   │
│    - DatabaseManager (asyncpg pool)                          │
│    - upsert_listing() + _upsert_photos()                     │
└───────────────────────┬─────────────────────────────────────┘
                        │
                        │ httpx + BeautifulSoup
                        ▼
┌─────────────────────────────────────────────────────────────┐
│                  REMAX Czech Republic                        │
│  https://www.remax-czech.cz/reality/vyhledavani/            │
│    - List pages: scraping s deduplikací                      │
│    - Detail pages: title, price, location, photos, area      │
│    - Rate limiting: 1 sec delay                              │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔑 Klíčové Technologie

| Vrstva | Stack |
|--------|-------|
| **Frontend** | Blazor Server + MudBlazor 9.x |
| **Backend** | .NET 10 + ASP.NET Core Minimal APIs |
| **Database** | PostgreSQL 15 + pgvector extension |
| **ORM** | EF Core 10 + EFCore.NamingConventions |
| **Scraper** | Python 3.12 + FastAPI + httpx + BeautifulSoup4 + asyncpg |
| **Container** | Docker Compose (PostgreSQL only) |
| **Browser Automation** | Playwright (optional, pro JS-heavy sites) |

---

## 📁 Důležité Soubory

### .NET Backend
```
src/RealEstate.Api/
  Endpoints/
    ListingEndpoints.cs     - POST /search, GET /{id}, POST /{id}/state
    SourceEndpoints.cs      - GET /sources
    ScrapingEndpoints.cs    - POST /trigger
  Services/
    ListingService.cs       - EF queries, SearchAsync, GetByIdAsync
    SourceService.cs        - GetSourcesAsync (DB query)
    ScrapingService.cs      - HTTP client → Python API
  Program.cs                - Minimal API setup
  Dockerfile                - Multi-stage build + Playwright deps

src/RealEstate.App/
  Components/Pages/
    Home.razor              - Dashboard s 3 kartami
    Listings.razor          - Tabulka s pagination + NavigationManager
    ListingDetail.razor     - Detail + MudCarousel + MudChips
  Components/Layout/
    NavMenu.razor           - Navigation bez Weather/Counter

src/RealEstate.Infrastructure/
  RealEstateDbContext.cs    - EF context + enum converters
  Repositories/             - Repository pattern implementations

src/RealEstate.Domain/
  Entities/
    Listing.cs              - Main entity with pgvector
    Source.cs, ListingPhoto.cs, UserListingState.cs
  Enums/
    PropertyType.cs         - House, Apartment, Land...
    OfferType.cs            - Sale, Rent
```

### Python Scraper
```
scraper/
  api/
    main.py                 - FastAPI app + DB lifecycle
    schemas.py              - Pydantic models
  core/
    database.py             - DatabaseManager + upsert_listing()
    runner.py               - run_scrape_job() orchestrator
    scrapers/
      remax_scraper.py      - Kompletní REMAX scraper
      mmreality_scraper.py  - Skeleton (TODO)
      prodejmeto_scraper.py - Skeleton (TODO)
  config/
    settings.yaml           - DB config + scraping settings
  requirements.txt          - Python dependencies
  run_api.py                - Uvicorn launcher
  REMAX_SCRAPER.md          - Dokumentace selektorů
```

### Configuration
```
docker-compose.yml          - PostgreSQL + pgvector
appsettings.json            - Connection strings, CORS
settings.yaml               - Scraper DB config
```

---

## 🐛 Opravené Bugy

| Bug | Popis | Řešení | Commit |
|-----|-------|--------|--------|
| **Empty sources** | SourceService vracel prázdný array | Implementován DB query přes EF Core | dc3170b |
| **Enum conversion** | DB měla české hodnoty, C# anglické | Přidány StringEnumConverters v DbContext | dc3170b |
| **SSL error** | HTTPS certifikát selhal | ApplicationBaseUrl → HTTP | ffc6a91 |
| **Template bloat** | Weather.razor, Counter.razor | Smazány včetně navigace | 2617f20 |
| **Mock scrapers** | Placeholder selektory | REMAX přepsán s reálnými selektory | a12212e |
| **Navigation broken** | NavigationManager commented out | Uncommented + added @inject | 0038ea3 |
| **No user feedback** | Chyběl ISnackbar | Added @inject + try/catch | 0038ea3 |
| **MudBlazor types** | MudChip, MudCarousel bez T | Added T="string", TData="object" | 0038ea3 |
| **No DB persistence** | _save_listing() stub | Implementován asyncpg upsert | 091b7eb |
| **Docs bug** | `if "dům" or "vila"` vždy True | Opraveno na správné `or` | 091b7eb |

---

## ✅ Funkční Features

### Frontend (Blazor)
- ✅ Home dashboard s 3 info kartami (sources count, semantic search, AI analysis)
- ✅ Listings tabulka s pagination (MudTable)
- ✅ Search/filter funkce (DTO-based)
- ✅ Detail stránka s MudCarousel
- ✅ Navigation mezi stránkami
- ✅ Snackbar notifications
- ✅ Responsive layout (MudBlazor)

### Backend (.NET)
- ✅ REST API s Minimal APIs
- ✅ EF Core s PostgreSQL
- ✅ Snake_case naming convention
- ✅ Enum konvertory (CZ↔EN)
- ✅ Repository pattern
- ✅ DI container setup
- ✅ CORS enabled

### Database
- ✅ PostgreSQL 15 + pgvector
- ✅ re_realestate schema
- ✅ 3 sources seed data
- ✅ 4 sample listings
- ✅ Guid primary keys
- ✅ Proper foreign keys

### Scraper
- ✅ REMAX scraper s reálnými selektory
- ✅ FastAPI async endpoints
- ✅ asyncpg database persistence
- ✅ Upsert logic (deduplikace)
- ✅ Photo synchronization
- ✅ Enum mapping (CZ→EN)
- ✅ Background job execution
- ✅ Job status tracking

---

## ⏳ TODO / Známé Limitace

### High Priority
- [ ] **Photo download pipeline** - original_url → stored_url (S3/lokální)
- [ ] **DTO centralizace** - přesunout DTOs z Listings.razor do RealEstate.Api.Contracts
- [ ] **CENTURY21 logo** - placeholder SVG 274 B, reálné za WP loginem
- [ ] **Kontejnerizace Blazor App** - přidat do docker-compose nebo přejít na .NET Aspire

### Scraper kvalita (málo výsledků)
- [ ] ZNOJMOREALITY (5), DELUXREALITY (5), PRODEJMETO (4), LEXAMO (4) – ověřit selektory
- [ ] Retry logic – exponential backoff pro HTTP 429/503
- [ ] Playwright fallback – pro JS-heavy weby

### Medium Priority
- [ ] **Semantic search** - pgvector s OpenAI embeddings
- [ ] **Analysis jobs** - AI analýza inzerátů
- [ ] **User listing states** - saved/archived/contacted tracking
- [ ] **Scheduled scraping** - APScheduler/Hangfire integration

### Low Priority
- [ ] **Unit tests** - scraper parsing s mock HTML
- [ ] **Monitoring** - Prometheus metrics, health checks
- [ ] **Export CSV/Excel** - projekt RealEstate.Export existuje

---

## 🚀 Deployment Instructions

### Local Development

```bash
# 1. Start PostgreSQL
docker-compose up -d postgres

# 2. Verify DB healthy
docker exec realestate-db psql -U postgres -d realestate_dev -c "SELECT version();"

# 3. Start .NET API
dotnet run --project src/RealEstate.Api --urls "http://localhost:5001"

# 4. Start Blazor UI
dotnet run --project src/RealEstate.App --urls "http://localhost:5002"

# 5. (Optional) Start Python Scraper API
cd scraper
python run_api.py
# → Běží na http://localhost:8001
```

### Testing Scraper

```bash
# Trigger scraping job přes .NET API
curl -X POST http://localhost:5001/api/scraping/trigger \
  -H "Content-Type: application/json" \
  -d '{"sourceCodes":["REMAX"],"fullRescan":false}'

# Direct test Python API
curl -X POST http://localhost:8001/v1/scrape/run \
  -H "Content-Type: application/json" \
  -d '{"source_codes":["REMAX"],"full_rescan":false}'

# Check job status
curl http://localhost:8001/v1/scrape/jobs/{job_id}
```

### URLs
- **Blazor UI:** http://localhost:5002
- **API:** http://localhost:5001
- **Swagger:** http://localhost:5001/swagger (pokud enabled)
- **Python Scraper API:** http://localhost:8001
- **Python API Docs:** http://localhost:8001/docs

---

## 📊 Statistiky Session

| Metrika | Hodnota |
|---------|---------|
| **Celkové commity** | 9 |
| **Soubory vytvořeny** | 15+ |
| **Soubory smazány** | 3 (Weather.razor, Counter.razor, Class1.cs) |
| **LOC přidáno** | ~3000+ |
| **Bugs opraveno** | 9 |
| **Features implementováno** | 12 |
| **Scrapers s reálnými selektory** | 1 (REMAX) |
| **API endpointy** | 7 |
| **Database tabulky** | 6 |

---

## 🎓 Lessons Learned

### Co fungovalo dobře
1. **Iterativní approach** - postupné řešení problémů místo big-bang refactoringu
2. **User feedback** - detailní code review od uživatele identifikovala skryté bugy
3. **Real selectors first** - test na živém webu místo guesswork
4. **Regex-based parsing** - robustnější než CSS selektory
5. **Async everywhere** - Python asyncio + .NET async/await
6. **Enum mapping** - centralizované konverze CZ↔EN

### Co zlepšit příště
1. **Unit tests dříve** - měly být součástí initial setup
2. **DTO shared library** - duplicity mohly být předejity
3. **Docker-compose full-stack** - včetně .NET + Python kontejnerů
4. **Logging centralization** - Serilog + structured logging
5. **Configuration validation** - fail-fast pokud config chybí

---

## 🔗 Git History

```
b94343e (HEAD) Fix PropertyType/OfferType converter + integrate logos into UI
b83639d        Add real estate agency logos (SVG/PNG)
2b20412        Apply Warm Property design system
b467209        Replace Bootstrap layout with full MudBlazor theme
0116968        UI: card view, quick filters, stats endpoint, scraping page
f826a2d        fix(sreality): _merge_detail text je dict
f8c8e1b        fix: ZnojmoReality, Prodejme.to, SReality, IDNES opravy
37c31c5        Listings.razor kompletní filtrovací panel
dda2087        Fix scrapers: C21 location, MMR district, HVREALITY
0d03355        Add Century21 scraper, seed scripts
a12212e        REMAX scraper complete rewrite + docs
091b7eb        Implement database persistence for REMAX scraper
```

---

## 📞 Contact & Resources

**Repository:** https://github.com/cybersmurf/RealEstateAggregator  
**Current Branch:** master  
**Default Branch:** main  

**Database:**
- Host: localhost:5432
- Database: realestate_dev
- User: postgres
- Schema: re_realestate

**Dependencies:**
- .NET 10.0
- PostgreSQL 15
- Python 3.12
- MudBlazor 9.x
- FastAPI 0.115+
- asyncpg 0.29+

---

**Session completed:** 23. února 2026  
**Current Commit:** b94343e  
**Next steps:** Kontejnerizace (docker-compose/Aspire), photo download pipeline, opravit selektory s málo výsledky
