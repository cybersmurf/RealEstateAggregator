# Analýza projektu Real Estate Aggregator

**Datum analýzy**: 22. února 2026  
**Verze**: 1.0.0-alpha  
**Autor**: GitHub Copilot

---

## 📊 Aktuální stav projektu (Summary)

### ✅ Co je hotovo (70%)

#### 1. **Projektová struktura** ✅ 100%
- .NET 9 Solution s 6 projekty (Api, App, Domain, Infrastructure, Background, Tests)
- Python scraper struktura s modulární architekturou
- Kompletní adresářová hierarchie
- Docker Compose orchestrace pro celý stack
- Dokumentace (README, TECHNICAL_DESIGN, BACKLOG, API_CONTRACTS, DEPLOYMENT)

#### 2. **Doménový model** ✅ 100%
- **Entities**: 
  - `Source` - zdroje realitních kanceláří
  - `Listing` - realitní inzeráty
  - `ListingPhoto` - fotografie inzerátů
  - `UserListingState` - uživatelské stavy (like/dislike)
  - `AnalysisJob` - AI analýzy
- **Enums**: PropertyType, OfferType, ConstructionType, Condition, ListingStatus, AnalysisStatus
- **Repositories**: Interface + implementace pro Listing

#### 3. **Infrastructure Layer** ✅ 90%
- `RealEstateDbContext` s kompletní konfigurací entit
- Entity Framework Core 9 integrace
- PostgreSQL provider (Npgsql)
- Repository pattern implementován
- Connection string management

**CHYBÍ**: EF Core migrations, seed data

#### 4. **Application Layer** ✅ 80%
- ASP.NET Core Web API (Program.cs, ServiceCollectionExtensions)
- Services: ListingService, SourceService, AnalysisService, ScrapingService
- DTOs a kontrakty (Contracts/*)
- API endpoints (ScrapingEndpoints, ListingEndpoints, SourceEndpoints, AnalysisEndpoints)
- Swagger/OpenAPI dokumentace
- PredicateBuilder pro pokročilé filtrování

**CHYBÍ**: Kompletní validace, error handling middleware, authentication

#### 5. **Presentation Layer** ✅ 60%
- Blazor Web App s MudBlazor 9
- Stránky: Home, Listings, Counter, Weather, Error, NotFound
- Základní layout a navigace
- HTTP client konfigurace

**CHYBÍ**: Plná funkcionalita Listings page, detail view, pokročilé filtry, responsivní design

#### 6. **Python Scraper** ✅ 40%
- Strukturace: core/, scrapers/, config/, api/
- BaseScraperu koncept (async, metrics)
- RemaxScraper částečně implementován (list pages + hybrid HTTP/Playwright)
- Browser manager (Playwright integration)
- Utils (timer, metrics)
- FastAPI pro scraping API

**CHYBÍ**: MM Reality scraper, Prodejme.to scraper, scheduler, error recovery, DB persistence

#### 7. **Docker & DevOps** ✅ 90%
- docker-compose.yml (PostgreSQL, API, Scraper, pgAdmin)
- Health checks pro PostgreSQL
- Volume management
- Network isolation
- .gitignore (Python + .NET)

**CHYBÍ**: Dockerfile pro .NET API, Dockerfile pro Python scraper, CI/CD pipeline

---

## ❌ Co chybí (30%)

### 1. **Database & Migrations** ⚠️ KRITICKÉ
- [ ] EF Core Initial Migration
- [ ] Seed data pro Sources (Remax, MM Reality, Prodejme.to)
- [ ] Databázové indexy performance tuning
- [ ] Migration apply skripty pro produkci

### 2. **Python Scrapers** ⚠️ VYSOKÁ PRIORITA
- [ ] MM Reality scraper implementace
- [ ] Prodejme.to scraper implementace
- [ ] Scheduler (APScheduler nebo cron-based)
- [ ] Error handling a retry logika
- [ ] Rate limiting a respectování robots.txt
- [ ] Persistence do PostgreSQL (INSERT/UPSERT listings)

### 3. **Blazor Frontend** ⚠️ VYSOKÁ PRIORITA
- [ ] Listings page - kompletní funkcionalita
  - [ ] Pokročilé filtry (cena, lokalita, typ, plocha)
  - [ ] Stránkování a sorting
  - [ ] Responsive grid layout
- [ ] Listing detail page
  - [ ] Fotogalerie
  - [ ] Mapa (Google Maps nebo OpenStreetMap)
  - [ ] Like/dislike buttons
  - [ ] Poznámky
- [ ] Dashboard (stats, charts)
- [ ] Analysis page (export to cloud, AI results)

### 4. **Background Services** ⚠️ STŘEDNÍ PRIORITA
- [ ] AnalysisJobProcessor (IHostedService)
- [ ] CloudStorageUploader (Google Drive / OneDrive)
- [ ] Periodic scraping job trigger
- [ ] Cleanup job (starých inzerátů)

### 5. **Cloud Integration** ⚠️ STŘEDNÍ PRIORITA
- [ ] Google Drive API integration
  - [ ] Authentication (OAuth 2.0)
  - [ ] Upload fotek + metadata
  - [ ] Create analysis folders
- [ ] Microsoft Graph API (OneDrive) - alternativa
- [ ] Export entity to Google Docs/Word

### 6. **Testing** ⚠️ STŘEDNÍ PRIORITA
- [ ] Unit testy pro Services
- [ ] Integration testy pro Repositories
- [ ] End-to-end testy pro API endpoints
- [ ] Python scraper testy (mock HTTP responses)
- [ ] Test coverage > 70%

### 7. **Authentication & Authorization** ⚠️ NÍZKÁ PRIORITA (MVP nepotřebuje)
- [ ] User management
- [ ] ASP.NET Identity
- [ ] JWT tokens
- [ ] Role-based access control

### 8. **Production Ready** ⚠️ NÍZKÁ PRIORITA
- [ ] Logging (Serilog, structured logging)
- [ ] Monitoring (Application Insights / Prometheus)
- [ ] Health checks endpoints
- [ ] API rate limiting
- [ ] CORS policies
- [ ] HTTPS enforcement
- [ ] Secret management (Azure Key Vault / AWS Secrets Manager)

---

## 🎯 Doporučené další kroky

### **Sprint 1: Minimal Viable Product (MVP)** - 2 týdny

#### Fáze 1: Database & Infrastructure (3 dny)
1. ✅ **Vytvořit EF Core migrations**
   ```bash
   cd src/RealEstate.Infrastructure
   dotnet ef migrations add InitialCreate --startup-project ../RealEstate.Api
   dotnet ef database update --startup-project ../RealEstate.Api
   ```

2. ✅ **Seed data pro Sources**
   - Vytvořit `DbInitializer.cs`
   - Přidat 3 sources: Remax, MM Reality, Prodejme.to
   - Spustit při aplikačním startu v Development mode

3. ✅ **Dockerfiles**
   - `src/RealEstate.Api/Dockerfile` (multi-stage build)
   - `scraper/Dockerfile` (Python 3.12+)
   - Test docker-compose up

#### Fáze 2: Scraping Implementation (4 dny)
4. ✅ **Dokončit RemaxScraper**
   - Detail page parsing
   - Photo extraction
   - DB persistence (INSERT/UPDATE)
   - Error handling

5. ✅ **Implementovat MM Reality scraper**
   - List pages + detail pages
   - Same logic jako Remax
   - DB persistence

6. ✅ **Scheduler**
   - Jednoduchý APScheduler job
   - Spustit scrapery každých 6 hodin
   - Logy do console + souboru

7. ✅ **Test end-to-end**
   - Spustit scraper → ověřit data v DB
   - curl API → získat listings
   - Blazor UI → zobrazit listings

#### Fáze 3: Frontend Polish (3 dny)
8. ✅ **Listings page**
   - MudDataGrid s pokročilými filtry
   - Sorting, paging
   - Responsive cards layout
   - Like/dislike buttons (User states)

9. ✅ **Listing detail page**
   - Route `/listing/{id}`
   - Fotogalerie (MudCarousel)
   - Všechny atributy zobrazit
   - Link na původní inzerát

10. ✅ **Basic dashboard**
    - Stats: celkem inzerátů, nové za týden, průměrná cena
    - Chart: ceny v čase (MudBlazor chart)

#### Fáze 4: Testing & Refinement (2 dny)
11. ✅ **Unit testy**
    - ListingService tests (mock repository)
    - ListingRepository tests (in-memory DB)
    - Coverage > 50%

12. ✅ **E2E test**
    - Selenium/Playwright test: search → detail → like
    - CI/CD: GitHub Actions basic workflow

---

### **Sprint 2: Cloud Integration & AI** - 2 týdny

13. ✅ **Google Drive API**
    - OAuth 2.0 setup
    - Upload listing + photos
    - Create folder structure

14. ✅ **AnalysisJob processor**
    - Background service (IHostedService)
    - Queue pattern (in-memory nebo RabbitMQ)
    - Export to cloud → queue for AI

15. ✅ **AI Integration Placeholder**
    - Manual trigger
    - Upload to Drive
    - Save Drive link v AnalysisJob entity

---

### **Sprint 3: Production Deployment** - 1 týden

16. ✅ **Logging & Monitoring**
    - Serilog structured logging
    - Application Insights nebo Prometheus

17. ✅ **Azure/AWS Deployment**
    - App Service / EC2 + RDS
    - Blob Storage pro fotky
    - CI/CD pipeline (GitHub Actions)

18. ✅ **Security Hardening**
    - HTTPS enforcement
    - Secret management
    - Rate limiting
    - CORS

---

## 📈 Metriky projektu

### Kód statistiky
| Kategorie | Soubory | Řádky kódu (odhad) | % Hotovo |
|-----------|---------|---------------------|----------|
| .NET Domain | 10 | ~500 | 100% |
| .NET Infrastructure | 5 | ~400 | 90% |
| .NET API | 8 | ~600 | 80% |
| .NET Blazor App | 10 | ~800 | 60% |
| Python Scrapers | 6 | ~600 | 40% |
| Docker & Config | 4 | ~200 | 90% |
| **CELKEM** | **43** | **~3100** | **75%** |

### Backlog progress
- **Celkem User Stories**: 90+
- **Story Points**: 165
- **Hotovo SP**: ~115 (70%)
- **Zbývá SP**: ~50 (30%)

---

## 🚨 Kritická rizika

### 1. **Chybí EF Core migrace** 🔴
**Dopad**: Aplikace nemůže běžet bez databáze  
**Řešení**: Vytvořit migrations jako PRVNÍ krok

### 2. **Scraping neukládá data do DB** 🔴
**Dopad**: Python scraper scrapuje, ale data se neukládají  
**Řešení**: Implementovat DB persistence v scraper/core/db.py

### 3. **Blazor UI není funkční** 🟡
**Dopad**: Nelze zobrazit listings, i když jsou v DB  
**Řešení**: Dodělat Listings.razor komponentu (filtry, paging)

### 4. **Dockerfiles chybí** 🟡
**Dopad**: docker-compose.yml nefunguje  
**Řešení**: Vytvořit Dockerfile pro .NET API a Python scraper

---

## ✅ Závěry a doporučení

### Co funguje dobře:
- ✅ Architektura je čistá (Domain-Driven Design, Repository pattern)
- ✅ Separace concerns (.NET backend, Python scraping)
- ✅ Dokumentace je vynikající
- ✅ Moderní stack (.NET 9, Python 3.12, PostgreSQL, MudBlazor)

### Co potřebuje pozornost:
- ⚠️ **PRIORITA 1**: Vytvořit EF migrations + seed data
- ⚠️ **PRIORITA 2**: Dokončit Python scrapers (DB persistence)
- ⚠️ **PRIORITA 3**: Dokončit Blazor UI (Listings page)
- ⚠️ **PRIORITA 4**: Dockerfiles a docker-compose test

### Doporučení pro další práci:
1. **Pracuj iterativně**: Nejdřív MVP (database + scraping + basic UI)
2. **Testuj průběžně**: Po každém sprintu end-to-end test
3. **Dokončuj moduly**: Radši 2 scrapers fungující než 5 nedokončených
4. **Deploy early**: Co nejdřív deploy do Azure/AWS pro feedback

---

## 🎯 Návrh roadmap

```
Sprint 1 (2 týdny): MVP
├─ EF Migrations + Seed ✅
├─ Python Scrapers (Remax + MM) ✅
├─ Blazor Listings Page ✅
└─ E2E Test ✅

Sprint 2 (2 týdny): Cloud & AI
├─ Google Drive Integration ✅
├─ Analysis Background Job ✅
└─ Production Logging ✅

Sprint 3 (1 týden): Deployment
├─ Azure/AWS Deploy ✅
├─ CI/CD Pipeline ✅
└─ Monitoring ✅

Future Sprints:
├─ Advanced Filters & Search
├─ User Authentication
├─ Mobile App (MAUI)
└─ AI-Powered Recommendations
```

---

**Konec analýzy**  
Pro otázky a diskuzi viz GitHub Issues nebo product backlog.
