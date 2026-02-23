# AI Session Summary – RealEstateAggregator
**Datum:** 23. února 2026  
**Celková doba:** ~8 hodin (3 sessions)  
**Celkové commity:** 25+  
**Status:** ✅ Production-ready full-stack aplikace, 12 scraperů, 1 236 aktivních inzerátů, Docker stack plně funkční

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
