# Product Backlog - Real Estate Aggregator

**Projekt**: Real Estate Aggregator  
**Verze**: 1.3.0  
**Datum**: 23. února 2026  
**Stav:** 12 scraperů, 1 236 inzerátů, "Warm Property" UI design, security/performance/stability fixes, 39 unit testů

> **Aktualizace Session 4 (23. 02.):** Implementovány všechny položky z hloubkové analýzy:  
> health endpoint + CORS + API key, tsvector fulltext, Filtered Include, HTTP retry (tenacity), CancellationToken, SourceDto refactor, 39 unit testů.
>
> **Aktualizace Session 3 (23. 02.):** Opraveny PropertyType/OfferType filtry (EF Core HasConversion bug).  
> 5 nových scraperů přidáno. Loga všech 12 zdrojů integrována do UI. Docker plně funkční.

---

## 🎯 Produktové vize

Vytvořit agregátor realitních inzerátů, který automaticky scrapuje vybrané weby realitek, normalizuje data do jednotného formátu a umožňuje pokročilé vyhledávání, filtrování a správu inzerátů s podporou AI analýzy.

---

## 📊 Prioritizace

| Priorita | Kategorie | Popis |
|----------|-----------|-------|
| P0 | Must Have | Funkce kritické pro MVP |
| P1 | Should Have | Důležité pro plnou funkcionalitu |
| P2 | Nice to Have | Vylepšení UX |
| P3 | Future | Budoucí rozšíření |

---

## 🏃 Sprint 0: Příprava infrastruktury

### EPIC-0: Projektový setup
**Priorita**: P0  
**Story Points**: 13

#### US-001: Vytvořit .NET solution strukturu
**Jako** developer  
**Chci** mít připravenou .NET solution s projekty  
**Abych** mohl začít implementovat backend a frontend

**Acceptance Criteria**:
- [x] Vytvořena .sln v rootu projektu
- [ ] Vytvořeny projekty: Api, App, Domain, Infrastructure, Background, Tests
- [ ] Všechny projekty se kompilují
- [ ] Nastaveny správné reference mezi projekty
- [ ] Přidány NuGet balíčky: EF Core, MudBlazor, Npgsql, Microsoft.Graph

**Tasks**:
- [ ] Vytvořit RealEstateAggregator.sln
- [ ] Vytvořit RealEstate.Api (ASP.NET Core Web API + Blazor)
- [ ] Vytvořit RealEstate.App (Blazor components)
- [ ] Vytvořit RealEstate.Domain (Class Library)
- [ ] Vytvořit RealEstate.Infrastructure (Class Library)
- [ ] Vytvořit RealEstate.Background (Class Library)
- [ ] Vytvořit RealEstate.Tests (xUnit)
- [ ] Nastavit project references
- [ ] Přidat NuGet balíčky

**Estimate**: 3 SP

---

#### US-002: Vytvořit Python scraper strukturu
**Jako** developer  
**Chci** mít připravený Python projekt pro scraping  
**Abych** mohl implementovat jednotlivé scrapery

**Acceptance Criteria**:
- [ ] Vytvořena struktura adresářů (scrapers/, core/, config/)
- [ ] requirements.txt s potřebnými závislostmi
- [ ] Virtuální prostředí funkční
- [ ] Base scraper interface/protokol
- [ ] Database connection module

**Tasks**:
- [ ] Vytvořit requirements.txt
- [ ] Implementovat core/models.py (data classes)
- [ ] Implementovat core/db.py (DB connection)
- [ ] Vytvořit base_scraper.py (Protocol)
- [ ] Vytvořit config/settings.yaml
- [ ] Dokumentace setup procesu

**Estimate**: 3 SP

---

#### US-003: Nastavit PostgreSQL databázi
**Jako** developer  
**Chci** mít připravenou databázi  
**Abych** mohl ukládat scrapovaná data a aplikační data

**Acceptance Criteria**:
- [ ] PostgreSQL 15+ běží (Docker nebo lokálně)
- [ ] Vytvořena databáze `realestate_dev`
- [ ] Connection string nakonfigurován v appsettings.json
- [ ] Connection string nakonfigurován v Python settings.yaml
- [ ] Test connection úspěšný z obou aplikací

**Tasks**:
- [ ] Připravit docker-compose.yml pro PostgreSQL
- [ ] Vytvořit init skripty pro DB
- [ ] Nastavit .NET connection string
- [ ] Nastavit Python connection string
- [ ] Vytvořit zdravotní check endpoint

**Estimate**: 2 SP

---

#### US-004: Nastavit Git repository a CI/CD
**Jako** developer  
**Chci** mít verzovaný kód s automatickými testy  
**Abych** měl kontrolu nad změnami a kvalitou kódu

**Acceptance Criteria**:
- [ ] Git repository inicializován
- [ ] .gitignore pro .NET a Python
- [ ] GitHub Actions workflow pro build a test
- [ ] Branch protection pravidla
- [ ] README.md s dokumentací

**Tasks**:
- [ ] git init + first commit
- [ ] Vytvořit .gitignore
- [ ] Vytvořit .github/workflows/dotnet.yml
- [ ] Vytvořit .github/workflows/python.yml
- [ ] Nastavit branch protection (main)

**Estimate**: 2 SP

---

#### US-005: Vytvořit Docker setup ✅ DONE (eb61e2d)
**Jako** developer  
**Chci** mít aplikaci v Dockeru  
**Abych** mohl snadno deployovat a spouštět celý stack

**Acceptance Criteria**:
- [x] Dockerfile pro .NET aplikaci
- [x] Dockerfile pro Python scraper
- [x] docker-compose.yml pro celý stack
- [x] Aplikace běží v kontejnerech
- [x] Dokumentace Docker commandů

**Tasks**:
- [x] Vytvořit src/RealEstate.Api/Dockerfile
- [x] Vytvořit scraper/Dockerfile
- [x] Vytvořit docker-compose.yml (app + scraper + db)
- [x] .dockerignore soubory
- [x] Dokumentovat spuštění

> **2026-02-23**: Kompletně dokončeno. Program.cs čte `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` z env; docker-compose je nastavuje na `postgres`.

**Estimate**: 3 SP

---

## 🏃 Sprint 1: Datový model a základní infrastruktura

### EPIC-1: Domain model a databáze
**Priorita**: P0  
**Story Points**: 21

#### US-101: Implementovat doménové entity
**Jako** developer  
**Chci** mít definované doménové entity  
**Abych** měl typově bezpečný datový model

**Acceptance Criteria**:
- [ ] Entity: Source, Listing, ListingPhoto, UserListingState, AnalysisJob
- [ ] Enums: PropertyType, OfferType, ConstructionType, Condition, ListingStatus, AnalysisStatus
- [ ] Validační logika v entitách
- [ ] Navigation properties správně nastaveny

**Tasks**:
- [ ] Vytvořit Domain/Entities/Source.cs
- [ ] Vytvořit Domain/Entities/Listing.cs
- [ ] Vytvořit Domain/Entities/ListingPhoto.cs
- [ ] Vytvořit Domain/Entities/UserListingState.cs
- [ ] Vytvořit Domain/Entities/AnalysisJob.cs
- [ ] Vytvořit Domain/Enums/*.cs
- [ ] Implementovat IEntity interface
- [ ] Unit testy pro validace

**Estimate**: 5 SP

---

#### US-102: Vytvořit EF Core DbContext a migrace
**Jako** developer  
**Chci** mít nakonfigurovaný EF Core  
**Abych** mohl pracovat s databází

**Acceptance Criteria**:
- [ ] RealEstateDbContext s DbSet pro všechny entity
- [ ] Entity configurations (fluent API)
- [ ] Indexy na klíčové sloupce (SourceId, ExternalId, LocationText, Price)
- [ ] Initial migration vytvořena
- [ ] Seed data pro Source (Remax, MM Reality, Prodejme.to)

**Tasks**:
- [ ] Vytvořit Infrastructure/Data/RealEstateDbContext.cs
- [ ] Vytvořit Infrastructure/Data/Configurations/*.cs
- [ ] Nakonfigurovat indexy a constraints
- [ ] dotnet ef migrations add Initial
- [ ] Vytvořit Infrastructure/Data/DbInitializer.cs pro seed
- [ ] Integration test pro DbContext

**Estimate**: 5 SP

---

#### US-103: Implementovat Repository pattern
**Jako** developer  
**Chci** mít abstrakci nad datovým přístupem  
**Abych** měl čistou separaci mezi logikou a perzistencí

**Acceptance Criteria**:
- [ ] IRepository<T> generic interface
- [ ] Repository<T> generic implementace
- [ ] Specialized repositories: IListingRepository, ISourceRepository
- [ ] Unit of Work pattern (volitelně)
- [ ] Asynchronní operace

**Tasks**:
- [ ] Vytvořit Domain/Repositories/IRepository.cs
- [ ] Vytvořit Domain/Repositories/IListingRepository.cs
- [ ] Vytvořit Infrastructure/Repositories/Repository.cs
- [ ] Vytvořit Infrastructure/Repositories/ListingRepository.cs
- [ ] Dependency injection registrace
- [ ] Integration testy

**Estimate**: 5 SP

---

#### US-104: Vytvořit Python data models
**Jako** scraper developer  
**Chci** mít Python modely odpovídající DB schématu  
**Abych** mohl ukládat data z scraperů

**Acceptance Criteria**:
- [ ] Dataclasses / Pydantic models pro všechny entity
- [ ] SQLAlchemy ORM modely (nebo asyncpg queries)
- [ ] Mapování Python → PostgreSQL typy
- [ ] Validace dat před uložením

**Tasks**:
- [ ] Vytvořit core/models.py (dataclasses)
- [ ] Vytvořit core/orm.py (SQLAlchemy models)
- [ ] Vytvořit core/db.py (session management)
- [ ] Implementovat create/update operace
- [ ] Unit testy pro models

**Estimate**: 3 SP

---

#### US-105: Implementovat DB migrace pro Python
**Jako** scraper developer  
**Chci** být schopný spustit migrace z Python strany  
**Abych** mohl vyvíjet scraper nezávisle

**Acceptance Criteria**:
- [ ] Alembic setup pro migrace
- [ ] Migrace synchronizované s EF Core
- [ ] CLI příkaz pro migrace
- [ ] Dokumentace použití

**Tasks**:
- [ ] pip install alembic
- [ ] alembic init
- [ ] Vytvořit env.py konfiguraci
- [ ] Vygenerovat initial migration
- [ ] Dokumentovat workflow

**Estimate**: 3 SP

---

## 🏃 Sprint 2: Backend API a služby

### EPIC-2: REST API
**Priorita**: P0  
**Story Points**: 21

#### US-201: Vytvořit API contracts (DTOs)
**Jako** API developer  
**Chci** mít definované DTO modely  
**Abych** měl jasný kontrakt mezi frontendem a backendem

**Acceptance Criteria**:
- [ ] ListingDto, ListingDetailDto, ListingSummaryDto
- [ ] ListingFilterDto (všechny filtry)
- [ ] UpdateUserStateDto
- [ ] AnalysisJobDto, CreateAnalysisDto
- [ ] PagedResultDto<T>
- [ ] AutoMapper profily

**Tasks**:
- [ ] Vytvořit Api/DTOs/ adresář
- [ ] Implementovat všechny DTO třídy
- [ ] Vytvořit Api/Mapping/MappingProfile.cs
- [ ] Nakonfigurovat AutoMapper
- [ ] Validační atributy ([Required], [Range], etc.)
- [ ] XML dokumentace pro Swagger

**Estimate**: 3 SP

---

#### US-202: Implementovat Listings API endpoint
**Jako** frontend developer  
**Chci** endpoint pro získání seznamu inzerátů  
**Abych** mohl zobrazit listing s filtry

**Acceptance Criteria**:
- [ ] GET /api/listings s paginací
- [ ] Filtry: sourceIds, region, priceMin/Max, areaMin/Max, propertyType, offerType, status
- [ ] Řazení: price, firstSeenAt, lastSeenAt
- [ ] Response: PagedResult<ListingSummaryDto>
- [ ] Swagger dokumentace

**Tasks**:
- [ ] Vytvořit Api/Controllers/ListingsController.cs
- [ ] Implementovat GetListings action
- [ ] Vytvořit Api/Services/IListingService.cs
- [ ] Implementovat ListingService s filtrační logikou
- [ ] EF Core query optimization (Select, Include)
- [ ] Integration test
- [ ] Swagger anotace

**Estimate**: 5 SP

---

#### US-203: Implementovat Listing detail API endpoint
**Jako** frontend developer  
**Chci** endpoint pro detail inzerátu  
**Abych** mohl zobrazit kompletní informace

**Acceptance Criteria**:
- [ ] GET /api/listings/{id}
- [ ] Response obsahuje: všechny parametry, fotky, user state, notes
- [ ] 404 pokud inzerát neexistuje
- [ ] Eager loading fotek a user state

**Tasks**:
- [ ] Implementovat GetListingById action
- [ ] Include ListingPhotos a UserListingState
- [ ] Error handling (404, 500)
- [ ] Integration test
- [ ] Swagger docs

**Estimate**: 2 SP

---

#### US-204: Implementovat User State API endpoint
**Jako** uživatel  
**Chci** ukládat stav inzerátů  
**Abych** mohl označovat favority a psát poznámky

**Acceptance Criteria**:
- [ ] POST /api/listings/{id}/state
- [ ] Body: { status: "Liked", notes: "Zajímavá lokalita" }
- [ ] Upsert logika (create nebo update)
- [ ] Validace statusu (enum)

**Tasks**:
- [ ] Implementovat UpdateListingState action
- [ ] Vytvořit UserStateService
- [ ] Upsert logika v repository
- [ ] Validace inputu
- [ ] Unit + integration test

**Estimate**: 3 SP

---

#### US-205: Implementovat Sources API endpoint
**Jako** frontend developer  
**Chci** endpoint pro seznam zdrojů  
**Abych** mohl zobrazit checkboxy pro filtrování

**Acceptance Criteria**:
- [ ] GET /api/sources
- [ ] Response: List<SourceDto> (id, name, isActive, logo URL)
- [ ] Seřazeno podle názvu
- [ ] Cache na 1 hodinu (in-memory)

**Tasks**:
- [ ] Vytvořit Api/Controllers/SourcesController.cs
- [ ] Implementovat GetSources action
- [ ] SourceDto
- [ ] Memory cache
- [ ] Integration test

**Estimate**: 2 SP

---

#### US-206: Implementovat vyhledávání (fulltext)
**Jako** uživatel  
**Chci** vyhledávat v inzerátech podle klíčových slov  
**Abych** našel specifické nemovitosti

**Acceptance Criteria**:
- [ ] Parametr `searchText` v GET /api/listings
- [ ] Vyhledávání v Title a Description
- [ ] Case-insensitive
- [ ] PostgreSQL ILIKE / tsvector

**Tasks**:
- [ ] Přidat searchText do ListingFilterDto
- [ ] Implementovat ILIKE query v ListingService
- [ ] (Volitelně) Přidat tsvector sloupec a GIN index
- [ ] Integration test s vyhledáváním
- [ ] Dokumentace

**Estimate**: 3 SP

---

#### US-207: Swagger UI konfigurace
**Jako** developer  
**Chci** mít interaktivní API dokumentaci  
**Abych** mohl testovat endpointy bez frontendu

**Acceptance Criteria**:
- [ ] Swagger UI na /swagger
- [ ] XML komentáře zobrazené v UI
- [ ] Příklady request/response
- [ ] Verze API v URL (/api/v1/)

**Tasks**:
- [ ] Nakonfigurovat Swashbuckle.AspNetCore
- [ ] Povolit XML documentation
- [ ] Přidat příklady do DTO
- [ ] Versioning middleware

**Estimate**: 2 SP

---

## 🏃 Sprint 3: Python Scraping

### EPIC-3: Web scraping
**Priorita**: P0  
**Story Points**: 21

#### US-301: Implementovat Remax scraper
**Jako** systém  
**Chci** scrapovat inzeráty z Remax  
**Abych** měl data z tohoto zdroje

**Acceptance Criteria**:
- [ ] RemaxScraper implementuje BaseScraper
- [ ] fetch_listings() - projde paginované výpisy
- [ ] fetch_listing_detail() - stáhne detail
- [ ] normalize() - parsuje do NormalizedListing
- [ ] Zpracuje minimálně: title, price, location, area, propertyType
- [ ] Stáhne URLs fotek
- [ ] Error handling (timeout, 404, parsing errors)

**Tasks**:
- [ ] Vytvořit scrapers/remax_scraper.py
- [ ] Implementovat listing parsing (BeautifulSoup)
- [ ] Implementovat detail parsing
- [ ] Normalizace dat (mapping енумů)
- [ ] Extrakce fotek
- [ ] Logging
- [ ] Unit testy s mock HTML

**Estimate**: 8 SP

---

#### US-302: Implementovat MM Reality scraper
**Jako** systém  
**Chci** scrapovat inzeráty z MM Reality  
**Abych** měl data z tohoto zdroje

**Acceptance Criteria**:
- Stejná jako US-301, ale pro MM Reality

**Tasks**:
- Analogické jako US-301

**Estimate**: 8 SP

---

#### US-303: Implementovat Prodejme.to scraper
**Jako** systém  
**Chci** scrapovat inzeráty z Prodejme.to  
**Abych** měl data z tohoto zdroje

**Acceptance Criteria**:
- Stejná jako US-301, ale pro Prodejme.to
- Prodejme.to může vyžadovat Playwright (JS rendering)

**Tasks**:
- [ ] Vytvořit scrapers/prodejme_to_scraper.py
- [ ] Setup Playwright (pokud potřeba)
- [ ] Implementovat scraping
- [ ] Unit testy

**Estimate**: 5 SP

---

## 🏃 Sprint 4: Scraping orchestrace

### EPIC-4: Scraper runner a scheduling
**Priorita**: P0  
**Story Points**: 13

#### US-401: Implementovat scraper runner
**Jako** systém  
**Chci** mít orchestraci všech scraperů  
**Abych** mohl spouštět scraping pravidelně

**Acceptance Criteria**:
- [ ] Runner projde všechny registrované scrapers
- [ ] Pro každý listing zkontroluje existenci v DB (SourceId + ExternalId)
- [ ] Nové inzeráty → INSERT + FirstSeenAt
- [ ] Existující → UPDATE + LastSeenAt
- [ ] Inzeráty neviděné 3× běhy → IsActive = false
- [ ] Logování: počet nových, updatovaných, chyb
- [ ] RunLog tabulka (start, end, stats)

**Tasks**:
- [ ] Vytvořit core/runner.py
- [ ] Registrace scraperů (dict/config)
- [ ] Upsert logika
- [ ] Deaktivace starých inzerátů
- [ ] RunLog model a ukládání
- [ ] CLI interface (argparse)
- [ ] Error handling a retry logika

**Estimate**: 8 SP

---

#### US-402: Implementovat scheduling (APScheduler)
**Jako** administrátor  
**Chci** automatické spouštění scraperu  
**Abych** nemusel ručně spouštět job

**Acceptance Criteria**:
- [ ] APScheduler konfigurace
- [ ] Cron výraz: 2× denně (např. 8:00, 20:00)
- [ ] Logging spuštění a dokončení
- [ ] Graceful shutdown
- [ ] Konfigurovatelný schedule (settings.yaml)

**Tasks**:
- [ ] pip install APScheduler
- [ ] Vytvořit core/scheduler.py
- [ ] Nakonfigurovat cron trigger
- [ ] Logging
- [ ] CLI příkaz pro spuštění scheduleru
- [ ] Dokumentace

**Estimate**: 3 SP

---

#### US-403: Monitoring a health check
**Jako** administrátor  
**Chci** vědět, jestli scraper běží správně  
**Abych** mohl reagovat na problémy

**Acceptance Criteria**:
- [ ] Health check endpoint (HTTP nebo soubor)
- [ ] Metriky: poslední běh, úspěch/fail, počet inzerátů
- [ ] Alert při selhání (email nebo log)

**Tasks**:
- [ ] Jednoduchý Flask/FastAPI endpoint pro health
- [ ] Uložení metrics do DB nebo souboru
- [ ] Email notifikace (SMTP)
- [ ] Dokumentace

**Estimate**: 2 SP

---

## 🏃 Sprint 5: Frontend - Blazor UI

### EPIC-5: MudBlazor UI
**Priorita**: P0  
**Story Points**: 21

#### US-501: Vytvořit layout a navigaci
**Jako** uživatel  
**Chci** mít konzistentní layout  
**Abych** se snadno orientoval v aplikaci

**Acceptance Criteria**:
- [ ] MudLayout s AppBar a Drawer
- [ ] Logo a název aplikace v AppBar
- [ ] Navigační menu: Dashboard, Analyzované inzeráty, Nastavení
- [ ] Responsivní design (mobile drawer)
- [ ] Dark/Light mode toggle

**Tasks**:
- [ ] Vytvořit App/Shared/MainLayout.razor
- [ ] MudAppBar component
- [ ] MudDrawer s menu items
- [ ] MudThemeProvider konfigurace
- [ ] Custom theme (barvy, fonts)

**Estimate**: 3 SP

---

#### US-502: Implementovat Dashboard (listing stránka)
**Jako** uživatel  
**Chci** vidět seznam inzerátů s filtry  
**Abych** našel zajímavé nemovitosti

**Acceptance Criteria**:
- [ ] MudDataGrid / MudTable s inzeráty
- [ ] Sloupce: zdroj (logo), titulek, lokalita, cena, plocha, pozemek, datum
- [ ] Paginace (stránkování)
- [ ] Řazení podle sloupců
- [ ] Row actions: Detail, Líbí/Nechci, Analýza
- [ ] Filtrovací panel (MudExpansionPanel)

**Tasks**:
- [ ] Vytvořit App/Pages/Dashboard.razor
- [ ] HttpClient service pro API volání
- [ ] ListingService (C# API wrapper)
- [ ] MudDataGrid konfigurace
- [ ] Loading state (MudProgressLinear)
- [ ] Error handling a toast notifikace

**Estimate**: 8 SP

---

#### US-503: Implementovat filtrační panel
**Jako** uživatel  
**Chci** filtrovat inzeráty podle různých kritérií  
**Abych** našel přesně to, co hledám

**Acceptance Criteria**:
- [ ] Filtry:
  - Region, District, Municipality (MudAutocomplete nebo MudSelect)
  - Cena od-do (MudNumericField)
  - Plocha od-do
  - Plocha pozemku od-do
  - Typ nemovitosti (checkboxy nebo MudSelect)
  - Typ nabídky (Prodej/Pronájem)
  - Zdroje (checkboxy)
  - Status (Nové, Oblíbené, ...)
- [ ] Tlačítka: Použít filtry, Vymazat
- [ ] Persisted state (localStorage)

**Tasks**:
- [ ] Vytvořit App/Components/FilterPanel.razor
- [ ] Two-way binding pro filter parametry
- [ ] Apply/Reset logika
- [ ] LocalStorage service pro ukládání
- [ ] Integrovat do Dashboard

**Estimate**: 5 SP

---

#### US-504: Implementovat detail inzerátu
**Jako** uživatel  
**Chci** vidět kompletní detail inzerátu  
**Abych** měl všechny informace

**Acceptance Criteria**:
- [ ] Modal dialog (MudDialog) nebo samostatná stránka
- [ ] Základní info card: název, cena, typ, lokalita
- [ ] Parametry tabulka: plocha, pozemek, stav, konstrukce, pokoje
- [ ] Carousel fotek (MudCarousel)
- [ ] Popis inzerátu (expandable)
- [ ] User state: dropdown (Nový/Líbí/Nechci/...), poznámky
- [ ] Akce: Otevřít originál, Udělat analýzu, Uložit poznámky

**Tasks**:
- [ ] Vytvořit App/Components/ListingDetailDialog.razor
- [ ] Layout s MudCard, MudCarousel
- [ ] State management pro user notes
- [ ] Save button funkčnost
- [ ] Integrovat do Dashboard (row click)

**Estimate**: 5 SP

---

## 🏃 Sprint 6: AI Analýza funkce

### EPIC-6: Analysis Job
**Priorita**: P1  
**Story Points**: 21

#### US-601: Vytvořit AnalysisJob entity a API
**Jako** developer  
**Chci** mít backend pro správu analýz  
**Abych** mohl spouštět a trackovat analýzy

**Acceptance Criteria**:
- [ ] POST /api/listings/{id}/analysis - vytvoří job
- [ ] GET /api/analysis/{jobId} - status jobu
- [ ] GET /api/analysis - seznam všech jobů (paginovaně)
- [ ] AnalysisJob tabulka v DB
- [ ] Status: Pending, Running, Succeeded, Failed

**Tasks**:
- [ ] Vytvořit Domain/Entities/AnalysisJob.cs
- [ ] Migrace pro AnalysisJob
- [ ] Api/Controllers/AnalysisController.cs
- [ ] AnalysisService interface a implementace
- [ ] DTOs: CreateAnalysisDto, AnalysisJobDto
- [ ] Integration testy

**Estimate**: 5 SP

---

#### US-602: Implementovat Background službu pro analýzu
**Jako** systém  
**Chci** asynchronně zpracovávat analýzy  
**Abych** neblokoval API requesty

**Acceptance Criteria**:
- [ ] IHostedService pro zpracování jobů
- [ ] Polling DB pro Pending joby
- [ ] Stažení listing data + fotek
- [ ] Generování dokumentu (Markdown/HTML)
- [ ] Nahrání na Google Drive / OneDrive
- [ ] Update job status na Succeeded/Failed
- [ ] Error handling a retry

**Tasks**:
- [ ] Vytvořit Background/Services/AnalysisBackgroundService.cs
- [ ] Implementovat job processing loop
- [ ] Vytvořit Background/Services/IDocumentGenerator.cs
- [ ] MarkdownDocumentGenerator implementation
- [ ] Integrace s cloud storage
- [ ] Logging a telemetrie
- [ ] Unit testy

**Estimate**: 8 SP

---

#### US-603: Integrace s Google Drive API
**Jako** systém  
**Chci** nahrávat dokumenty na Google Drive  
**Abych** měl data dostupná v cloudu

**Acceptance Criteria**:
- [ ] OAuth2 autentizace (service account nebo user flow)
- [ ] Upload souboru do specifické složky
- [ ] Generování shareable linku
- [ ] Error handling (quota, network errors)

**Tasks**:
- [ ] Vytvořit Infrastructure/CloudStorage/IGoogleDriveService.cs
- [ ] Implementovat GoogleDriveService
- [ ] Google.Apis.Drive.v3 NuGet
- [ ] OAuth setup (credentials.json)
- [ ] Konfigurace target folder ID
- [ ] Integration test (nebo manual test)
- [ ] Dokumentace setup

**Estimate**: 5 SP

---

#### US-604: Integrace s OneDrive (Microsoft Graph)
**Jako** systém  
**Chci** nahrávat dokumenty na OneDrive  
**Jako** alternativu k Google Drive

**Acceptance Criteria**:
- Analogické jako US-603, ale pro OneDrive

**Tasks**:
- [ ] Vytvořit Infrastructure/CloudStorage/IOneDriveService.cs
- [ ] Implementovat OneDriveService
- [ ] Microsoft.Graph NuGet
- [ ] Azure AD app registration
- [ ] Konfigurace
- [ ] Testy

**Estimate**: 5 SP

---

#### US-605: UI pro spuštění a zobrazení analýz
**Jako** uživatel  
**Chci** spustit analýzu inzerátu a vidět výsledek  
**Abych** měl podklady pro rozhodování

**Acceptance Criteria**:
- [ ] Tlačítko "Udělat analýzu" v detailu inzerátu
- [ ] Po kliknutí: konfirmační dialog, volání API
- [ ] Toast notifikace: "Analýza byla spuštěna"
- [ ] Polling každých 5s pro update statusu
- [ ] Když Succeeded: zobrazit tlačítko "Otevřít v Drive"
- [ ] (Volitelně) Stránka se seznamem všech analýz

**Tasks**:
- [ ] Přidat button do ListingDetailDialog
- [ ] Implementovat CreateAnalysis API call
- [ ] Polling logika (Timer)
- [ ] Status badge (Pending/Running/Succeeded/Failed)
- [ ] Link na cloud storage
- [ ] (Volitelně) App/Pages/Analyses.razor

**Estimate**: 3 SP

---

## 🏃 Sprint 7: Semantic Search & AI (pgvector)

### EPIC-7: pgvector Semantic Search
**Priorita**: P1  
**Story Points**: 21

#### US-701: Setup PostgreSQL pgvector extension
**Jako** developer  
**Chci** mít pgvector nainstalovaný v PostgreSQL  
**Abych** mohl ukládat embeddings

**Acceptance Criteria**:
- [ ] CREATE EXTENSION vector v databázi
- [ ] Migrace přidá description_embedding vector(1536) do listings
- [ ] HNSW index vytvořen pro rychlé similarity search
- [ ] Test query funguje (dummy embedding)

**Tasks**:
- [ ] Aktualizovat init-db.sql s CREATE EXTENSION vector
- [ ] Vytvořit EF Core migration pro description_embedding column
- [ ] Vytvořit HNSW index (m=16, ef_construction=64)
- [ ] Seed dummy data s embeddings pro testování
- [ ] Dokumentace v README

**Estimate**: 3 SP

---

#### US-702: Implementovat OpenAI Embeddings Service
**Jako** systém  
**Chci** generovat embeddings z textů inzerátů  
**Abych** mohl dělat semantic search

**Acceptance Criteria**:
- [ ] NuGet balíček OpenAI nainstalován
- [ ] IEmbeddingService interface
- [ ] EmbeddingService implementace s OpenAI Client
- [ ] Konfigurace API key v appsettings.json
- [ ] Model: text-embedding-3-small (1536 dimenzí)
- [ ] Error handling a retry logika
- [ ] Rate limiting (respektovat OpenAI limits)

**Tasks**:
- [ ] dotnet add package OpenAI
- [ ] Vytvořit Services/IEmbeddingService.cs
- [ ] Implementovat EmbeddingService
- [ ] appsettings.json konfigurace
- [ ] Unit testy (mock OpenAI responses)
- [ ] Integration test (skutečné API volání)
- [ ] Logging

**Estimate**: 5 SP

---

#### US-703: Implementovat pgvector repository v .NET
**Jako** developer  
**Chci** ukládat a dotazovat embeddings z .NET  
**Abych** mohl dělat similarity search

**Acceptance Criteria**:
- [ ] NuGet balíček Npgsql + Pgvector nainstalován
- [ ] NpgsqlDataSource nakonfigurován s UseVector()
- [ ] IListingEmbeddingRepository interface
- [ ] UpdateEmbeddingAsync(listingId, embedding) metoda
- [ ] SearchSimilarAsync(queryEmbedding, limit) metoda
- [ ] Použití Vector type z pgvector-dotnet
- [ ] Query optimalizace (WHERE is_active, LIMIT)

**Tasks**:
- [ ] dotnet add package Npgsql
- [ ] dotnet add package Pgvector
- [ ] Update ServiceCollectionExtensions s UseVector()
- [ ] Vytvořit Infrastructure/Repositories/ListingEmbeddingRepository.cs
- [ ] Implementovat UPSERT embedding logiku
- [ ] Implementovat semantic search query (<-> operátor)
- [ ] Unit + integration testy
- [ ] Performance testing (benchmark)

**Estimate**: 5 SP

---

#### US-704: Background job pro generování embeddingů
**Jako** systém  
**Chci** automaticky generovat embeddings pro nové inzeráty  
**Abych** měl data připravená pro semantic search

**Acceptance Criteria**:
- [ ] IHostedService pro embedding generation
- [ ] Každou hodinu zkontroluje listings bez embeddingu
- [ ] Generuj embeddings v dávkách (batch 100)
- [ ] Respektuj OpenAI rate limits (delay mezi calls)
- [ ] Update embedding do DB
- [ ] Logging progress a errors
- [ ] Graceful shutdown

**Tasks**:
- [ ] Vytvořit Background/Services/EmbeddingGeneratorService.cs
- [ ] Implementovat ExecuteAsync loop
- [ ] Repository metoda GetListingsWithoutEmbeddingAsync()
- [ ] Batch processing s rate limiting
- [ ] Error handling a retry
- [ ] Konfigurace intervalu (appsettings.json)
- [ ] Monitoring metrics
- [ ] Unit testy

**Estimate**: 5 SP

---

#### US-705: API endpoint pro semantic search
**Jako** frontend developer  
**Chci** endpoint pro semantic search  
**Abych** mohl implementovat "chytré" vyhledávání v UI

**Acceptance Criteria**:
- [ ] POST /api/semantic/search
- [ ] Request: { query: "volný text", limit: 20 }
- [ ] Response: List<ListingSummaryDto>
- [ ] Query → embedding → similarity search → DTOs
- [ ] Swagger dokumentace
- [ ] Performance monitoring

**Tasks**:
- [ ] Vytvořit Services/ISemanticSearchService.cs
- [ ] Implementovat SemanticSearchService
- [ ] Vytvořit Endpoints/SemanticSearchEndpoints.cs
- [ ] MapPost("/api/semantic/search")
- [ ] DTOs (SemanticSearchRequest, response)
- [ ] Integration test
- [ ] Swagger annotations
- [ ] Performance logging

**Estimate**: 3 SP

---

## 🏃 Sprint 8: Semantic Search UI & UX

### EPIC-8: Frontend Semantic Search
**Priorita**: P1  
**Story Points**: 13

#### US-801: Blazor UI pro semantic search
**Jako** uživatel  
**Chci** zadávat volný text přes UI a dostat relevantní inzeráty  
**Abych** našel nemovitosti bez složitých filtrů

**Acceptance Criteria**:
- [ ] MudTextField pro volný textový dotaz
- [ ] MudButton "AI Hledání" s ikonou
- [ ] Multi-line text area (2-3 řádky)
- [ ] Placeholder s příklady ("chci chalupu s velkým pozemkem...")
- [ ] Loading state při dotazu
- [ ] Zobrazení výsledků v tabulce/cards
- [ ] Toast notifikace při chybě

**Tasks**:
- [ ] Aktualizovat Pages/Listings.razor
- [ ] SemanticSearch sekce v UI
- [ ] HttpClient call na /api/semantic/search
- [ ] State management (_semanticQuery, _semanticResults)
- [ ] Error handling
- [ ] UX polish (icons, styling)

**Estimate**: 5 SP

---

#### US-802: Hybrid search (kombinace filtrů + semantic)
**Jako** uživatel  
**Chci** kombinovat semantic search s klasickými filtry  
**Abych** dostal přesné výsledky

**Acceptance Criteria**:
- [ ] Možnost zapnout/vypnout semantic mode
- [ ] Při semantic search respektovat aktivní filtry (region, cena)
- [ ] Backend kombinuje WHERE clauses + ORDER BY embedding
- [ ] Toggle button "🔍 Klasické" vs "🤖 AI Hledání"
- [ ] Vysvětlení rozdílu v UI (tooltip)

**Tasks**:
- [ ] Aktualizovat SemanticSearchService s predikáty
- [ ] SQL query kombinuje WHERE + ORDER BY <->
- [ ] Frontend toggle state
- [ ] Conditional rendering filtrů
- [ ] User education (help text)

**Estimate**: 5 SP

---

#### US-803: User preference embeddings
**Jako** uživatel  
**Chci** uložit své preference a dostat personalizované výsledky  
**Abych** nemusel zadávat query pokaždé

**Acceptance Criteria**:
- [ ] Stránka "Moje preference"
- [ ] TextArea pro popis preferencí
- [ ] Generování embedding z preference textu
- [ ] Uložení do user_preferences tabulky
- [ ] API endpoint POST /api/preferences
- [ ] API endpoint GET /api/preferences/matches (doporučené inzeráty)

**Tasks**:
- [ ] Vytvořit Domain/Entities/UserPreference.cs
- [ ] Migrace pro user_preferences tabulka
- [ ] Repository + Service
- [ ] API endpoints
- [ ] Frontend stránka Preferences.razor
- [ ] Background job pro matching (denně)
- [ ] Email notifikace o nových matches (volitelné)

**Estimate**: 8 SP

---

## 🏃 Sprint 9: Pokročilé funkce a UX vylepšení

### EPIC-9: UX a optimalizace
**Priorita**: P2  
**Story Points**: 13

#### US-901: Implementovat "Novinky" badge
**Jako** uživatel  
**Chci** vidět, které inzeráty jsou nové od posledního zobrazení  
**Abych** nepřehlédl zajímavé nemovitosti

**Acceptance Criteria**:
- [ ] Badge "NOVÉ" u inzerátů s FirstSeenAt > poslední návštěva
- [ ] Uložení lastVisitedAt per uživatel (nebo global)
- [ ] Počet nových inzerátů v navigaci

**Tasks**:
- [ ] UserSettings entita (lastVisitedAt)
- [ ] API endpoint pro update lastVisitedAt
- [ ] Frontend: badge rendering
- [ ] Counter v AppBar

**Estimate**: 3 SP

---

#### US-702: Export do PDF
**Jako** uživatel  
**Chci** exportovat inzerát do PDF  
**Abych** mohl tisknout nebo sdílet offline

**Acceptance Criteria**:
- [ ] Tlačítko "Export PDF" v detailu
- [ ] Vygenerované PDF obsahuje: parametry, popis, fotky
- [ ] Download PDF do browseru

**Tasks**:
- [ ] NuGet: QuestPDF nebo PuppeteerSharp
- [ ] PdfService implementace
- [ ] API endpoint: GET /api/listings/{id}/pdf
- [ ] Frontend: downloadování souboru
- [ ] Styling PDF

**Estimate**: 5 SP

---

#### US-703: Mapové zobrazení inzerátů
**Jako** uživatel  
**Chci** vidět inzeráty na mapě  
**Abych** lépe pochopil lokaci

**Acceptance Criteria**:
- [ ] Tab "Mapa" v Dashboard
- [ ] Leaflet.js nebo Google Maps integrace
- [ ] Piny pro jednotlivé inzeráty
- [ ] Popup s základními info při kliknutí
- [ ] Filtrování synchronizované s tabulkou

**Tasks**:
- [ ] Geocoding adres (Google Geocoding API nebo OpenStreetMap)
- [ ] Uložení lat/lng do Listing
- [ ] Blazor komponenta s mapou
- [ ] LeafletBlazor nebo JS interop
- [ ] Synchronizace filtrů

**Estimate**: 8 SP

---

## 🏃 Sprint 8: Autentizace a multi-user

### EPIC-8: User management
**Priorita**: P3  
**Story Points**: 21

#### US-801: Implementovat ASP.NET Identity
**Jako** systém  
**Chci** mít správu uživatelů  
**Abych** podporoval více uživatelů

**Acceptance Criteria**:
- [ ] ASP.NET Core Identity nakonfigurováno
- [ ] User tabulka v DB
- [ ] Registrace, přihlášení, odhlášení
- [ ] JWT tokeny nebo Cookie auth
- [ ] Password reset

**Tasks**:
- [ ] Přidat Microsoft.AspNetCore.Identity.EntityFrameworkCore
- [ ] Rozšířit DbContext o Identity
- [ ] Migrace
- [ ] Api/Controllers/AuthController.cs
- [ ] Login/Register endpoints
- [ ] Middleware pro JWT

**Estimate**: 8 SP

---

#### US-802: Uživatelské profily a nastavení
**Jako** uživatel  
**Chci** mít vlastní profil a nastavení  
**Abych** měl personalizovaný zážitek

**Acceptance Criteria**:
- [ ] UserProfile entita: email, preferredRegions, notifications
- [ ] API: GET/PUT /api/profile
- [ ] UI: Stránka Nastavení

**Tasks**:
- [ ] UserProfile entita
- [ ] ProfileController
- [ ] Frontend: App/Pages/Settings.razor
- [ ] Formulář pro update profilu

**Estimate**: 5 SP

---

#### US-803: Izolace UserListingState per uživatel
**Jako** uživatel  
**Chci** mít vlastní poznámky a stavy  
**Abych** je nesdílel s ostatními

**Acceptance Criteria**:
- [ ] UserListingState.UserId NOT NULL
- [ ] Filtry v API respektují UserId
- [ ] Migrace pro přidání UserId

**Tasks**:
- [ ] Změna UserListingState entity
- [ ] Migrace
- [ ] Update všech API endpointů
- [ ] Update UI

**Estimate**: 3 SP

---

#### US-804: Email notifikace o nových inzerátech
**Jako** uživatel  
**Chci** dostávat emaily o nových inzerátech  
**Abych** nepřehlédl zajímavé nabídky

**Acceptance Criteria**:
- [ ] Nastavení: "Posílat denní digest" (bool)
- [ ] Background služba: denní job
- [ ] Email template s novými inzeráty
- [ ] SMTP konfigurace

**Tasks**:
- [ ] NotificationService
- [ ] Email template (Razor)
- [ ] SMTP setup (MailKit nebo SendGrid)
- [ ] Background job (daily)
- [ ] Konfigurace v Settings

**Estimate**: 5 SP

---

## 🏃 Backlog - budoucí features (P3)

### US-901: Porovnání inzerátů vedle sebe
Možnost vybrat 2-3 inzeráty a porovnat je v tabulce.  
**Estimate**: 5 SP

### US-902: Import vlastních inzerátů (CSV/Excel)
Manuální nahrání inzerátů z jiných zdrojů.  
**Estimate**: 5 SP

### US-903: AI hodnocení inzerátu
Integrace s GPT-4 pro automatické hodnocení (cena vs. trh, výhody/nevýhody).  
**Estimate**: 8 SP

### US-904: Mobile app (MAUI)
Nativní mobilní aplikace pro iOS/Android.  
**Estimate**: 21 SP

### US-905: Push notifikace (WebPush)
Browser notifikace o nových inzerátech.  
**Estimate**: 5 SP

### US-906: Uložená vyhledávání
Možnost uložit filtr a rychle ho znovu použít.  
**Estimate**: 3 SP

### US-907: Sdílení inzerátu (link)
Vygenerovat publický link na inzerát.  
**Estimate**: 3 SP

### US-908: Scraping více RK
Přidat 10+ dalších realitních kanceláří.  
**Estimate**: 40 SP

---

## 📊 Celkový přehled Story Points

| Epic | Story Points | Priorita |
|------|--------------|----------|
| EPIC-0: Projektový setup | 13 | P0 |
| EPIC-1: Domain model a databáze | 21 | P0 |
| EPIC-2: REST API | 21 | P0 |
| EPIC-3: Web scraping | 21 | P0 |
| EPIC-4: Scraper orchestrace | 13 | P0 |
| EPIC-5: MudBlazor UI | 21 | P0 |
| EPIC-6: Analysis Job | 21 | P1 |
| EPIC-7: UX a optimalizace | 13 | P2 |
| EPIC-8: User management | 21 | P3 |
| **Celkem MVP (P0)** | **110** | - |
| **Celkem s P1** | **131** | - |
| **Celkem vše** | **165** | - |

---

## 🎯 Definition of Done

Každá user story je považována za hotovou, když:

- [x] Kód je napsán a otestován (unit + integration testy kde aplikovatelné)
- [x] Code review provedeno (pokud tým > 1)
- [x] Dokumentace aktualizována (README, tech docs)
- [x] API Swagger aktualizován (pokud API změna)
- [x] Migrace databáze vytvořeny a otestovány
- [x] UI je responsivní a funguje na mobilech
- [x] Žádné kritické bugs
- [x] Změny commitnuty do main branch
- [x] CI/CD pipeline prošel (build + test)

---

## 📅 Plánovaný harmonogram

| Sprint | Týdny | Cíl |
|--------|-------|-----|
| Sprint 0 | 1 | Infrastruktura ready |
| Sprint 1 | 2 | Databáze a domain model |
| Sprint 2 | 2 | API endpoints funkční |
| Sprint 3 | 2 | 3 scrapers implementovány |
| Sprint 4 | 1 | Scraping běží automaticky |
| Sprint 5 | 2 | UI kompletní pro základní funkce |
| Sprint 6 | 2 | AI analýza funkční |
| Sprint 7 | 1-2 | UX vylepšení |
| Sprint 8 | 2 | Multi-user podpora |

**Celkem: ~14-16 týdnů pro full feature set**  
**MVP (P0 pouze): ~10 týdnů**

---

## 🐛 Known Issues & Technical Debt

### Issue-REMAX-001: REMAX CSS Selektory nefrčí

**Popis**: RemaxListScraper vrací 0 inzerátů kvůli zastaralým CSS selektorům  
**Priorita**: P1 (blokuje scraping)  
**Reporter**: Debugging session 22.2.2026  
**Status**: Open

**Details**:
- Playwright dosáhne správné URL
- RemaxListScraper najde 0 prvků
- Fallback selektory: `.remax-search-result-item`, `.property-item`, `.realty-item`, `.search-result` → všechny vrací []
- REMAX HTML se změnil (poslední scraper commit: 6 měsíců zpět)

**Akční plán**:
1. [ ] Spustit RemaxListScraper s URL `hledani=2&regions[116][3713]=on`
2. [ ] Otevřít DevTools v Playwrightovi → `page.Screenshot()` do logs
3. [ ] Zjistit aktuální CSS strukturu list karet
4. [ ] Updatovat RemaxListScraper selektory
5. [ ] Test: Verifikovat >0 results
6. [ ] Similarly pro RemaxDetailScraper selektory

**Workaround**: Použít DirectUrl s direktním navigováním (zatím nefunguje)

---

### Issue-REMAX-002: Typ nemovitosti se vždy mapuje na "House"

**Popis**: RemaxImporter hardcoduje `PropertyType.House` a `OfferType.Sale` pro všechny inzeráty  
**Priorita**: P1 (datová integrita)  
**Reporter**: Architecture analysis 22.2.2026  
**Status**: Open

**Details**:
- RemaxDetailResult extraktor parsuje PropertyType a OfferType (jako stringy)
- RemaxImporter.MapToListingEntity() ignoruje tyto hodnoty
- Všechny inzeráty → House + Sale
- Ztráta informace o bytech (Apartment), pozemcích (Land), pronájmech (Rent)

**Příčina**: `MapToListingEntity()` (line ~140):
```csharp
var listing = new Listing
{
    // ... other fields ...
    PropertyType = PropertyType.House,  // ☝️ HARDCODED
    OfferType = OfferType.Sale          // ☝️ HARDCODED
};
```

**Řešení**:
1. [ ] V RemaxDetailScraper: Extrahovat PropertyType ze titulu (regex: "Dům|Byt|Pozemek")
2. [ ] V RemaxDetailResult: Přidat `string? ExtractedPropertyType { get; set; }`
3. [ ] V RemaxImporter: Implementovat detekci:
```csharp
var propertyType = ToPropertyType(detail.ExtractedPropertyType) ?? PropertyType.Other;
```
4. [ ] Similarly pro OfferType (parse z URL parametru nebo searchType)
5. [ ] Test: Scrape Brno byty → verifikovat PropertyType.Apartment

---

### Issue-REMAX-003: Chybí error handling pro failed details

**Popis**: Pokud RemaxDetailScraper selže na jednom detail, celý scrape session skončí  
**Priorita**: P2 (robustnost)  
**Reporter**: Code review  
**Status**: Open

**Impact**:
- 1 timeout/parse error → 0 listings úspěšně scrapeno
- Žádný partial success

**Řešení**:
1. [ ] Wrap `detailScraper.ScrapeDetailAsync()` v try/catch
2. [ ] Log error, continue to next item
3. [ ] Track failed detail URLs → retry později
4. [ ] Aggregate stats: "Succeeded: 45, Failed: 2, Total: 47"

---

### Issue-REMAX-004: Maximálně 20 fotek per inzerát

**Popis**: RemaxDetailScraper zvětšuje max 20 fotek  
**Priorita**: P2 (feature limit)  
**Reporter**: Code review  
**Status**: Design decision

**Details**:
- Limit: `.Take(20)` v ParsePhotos()
- Problém: Inzeráty mají často 30+ fotek
- Ztráta informace

**Řešení**:
- [ ] Zvýšit na 50 fotek
- [ ] Nebo: Store all URLs, display first 20, lazy-load kliknutím "Show more"

---

### Issue-REMAX-005: Photo URLs mohou expirovat

**Popis**: REMAX foto URL adresy obsahují relativní cesty; mohou být offline po měsících  
**Priorita**: P3 (UX issue)  
**Reporter**: Observations  
**Status**: Monitoring required

**Current Approach**:
- Store `original_url`: "https://mlsf.remax-czech.cz/file/123/photo.jpg"
- Lazy-load v UI

**Future Option**:
- Download image na S3/local storage
- Regular validation: cron job 1x měsíčně check URLs
- Auto-remove offline photos

---

### Issue-REMAX-006: Chybí pagination support v UI

**Popis**: RemaxScrapingProfileDto.MaxPages = 5 (default), ale API nemá endpoint pro scrape s konkrétní stránkou  
**Priorita**: P2 (feature gap)  
**Reporter**: Design analysis  
**Status**: Design needed

**Details**:
- RemaxImporter supports MaxPages parameter
- API `/api/scraping-playwright/run` vždy scrapuje default 5 stránek
- Potřebujeme: možnost nastavit MaxPages z UI

**Řešení**:
- [ ] Add `maxPages` field do RemaxScrapingProfileDto schema
- [ ] Update PlaywrightScrapingOrchestrator to respect maxPages
- [ ] Add UI control: slider 1-100 stran

---

### Issue-REMAX-007: Nebyl test pro URL building

**Popis**: RemaxScrapingService.BuildSearchUrl() logika bez unit testů  
**Priorita**: P2 (quality)  
**Reporter**: Code review  
**Status**: Blocked (needs test project setup)

**Test cases**:
- DirectUrl → ignore ostatní params
- RegionId=116 + DistrictId=3713 → region-based URL
- CityName="Praha" → fulltext URL
- Multiple filters combined → query string composition

**Řešení**:
- [ ] Přidat xUnit tests do RealEstate.Tests
- [ ] Mock IListingRepository
- [ ] Test URL generation scenarios

---

## 📍 Technical Debt

| Kategorie | Popis | Priorita |
|-----------|-------|----------|
| **Selektory** | REMAX CSS se mění, fallbacks nefrčí | P1 |
| **Type mapping** | Hardcoded House+Sale | P1 |
| **Error handling** | Fail-fast na detail error | P2 |
| **Photos** | Max 20 limit, expirování | P2 |
| **Pagination** | Fixní 5 stran, bez UI control | P2 |
| **Testing** | 0 unit tests pro scraping | P2 |
| **Python scraper** | Deprecated, není v use | P3 |
| **Playwright cache** | Nema disk cache pro HTML | P3 |

---

**Konec backlogu** • Verze 1.0 • 22. února 2026
