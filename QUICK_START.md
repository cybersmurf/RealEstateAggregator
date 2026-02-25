# Quick Start Guide - Real Estate Aggregator

**Vytvořeno**: 22. února 2026  
**Verze**: 1.1.0 (aktualizováno 25. února 2026 – RAG + MCP + Export retry)

---

## ✅ Co bylo vytvořeno

### 📐 Projektová struktura

```
RealEstateAggregator/
├── docs/
│   ├── BACKLOG.md              (Product backlog s user stories)
│   ├── TECHNICAL_DESIGN.md     (Technický návrh)
│   ├── API_CONTRACTS.md        (API dokumentace)
│   └── DEPLOYMENT.md           (Deployment guide)
│
├── src/
│   ├── RealEstate.Domain/      (Doménové entity a enums)
│   ├── RealEstate.Infrastructure/  (EF Core - připraveno)
│   ├── RealEstate.Background/  (Background služby - připraveno)
│   ├── RealEstate.Api/         (ASP.NET Core API - připraveno)
│   └── RealEstate.App/         (Blazor + MudBlazor - připraveno)
│
├── scraper/
│   ├── scrapers/               (Python scrapers - připraveno)
│   ├── core/                   (Core logika - připraveno)
│   ├── config/
│   │   └── settings.yaml       (Konfigurace)
│   └── requirements.txt        (Python dependencies)
│
├── tests/
│   └── RealEstate.Tests/       (Unit testy - připraveno)
│
├── RealEstateAggregator.sln    (Solution file)
├── docker-compose.yml          (Docker orchestrace)
├── .gitignore                  (Git ignore pravidla)
└── README.md                   (Hlavní dokumentace)
```

### 🎯 Hotovo

✅ **Kompletní projektová struktura**
- .NET 10 solution s 6 projekty
- Python scraper struktura
- Všechny adresáře a šablony

✅ **Doménový model**
- 6 entit: Source, Listing, ListingPhoto, UserListingState, AnalysisJob, ScrapeRun
- pgvector support pro semantic search (1536-dim embeddings)
- Všechny typy synchronizované (double Pro plochy, decimal pro ceny, string pro enums)

✅ **Entity Framework Core 10 s pgvector**
- Kompletní DbContext s re_realestate schema namespacing
- Tabulky mapované na PostgreSQL s pgvector support
- HNSW index na description_embedding (L2 distance)
- Full-text search s generated tsvector column
- Foreign keys, cascade deletes, unique constraints
- EF migrations vygenerovaný (`InitialSchema`)
- SQL migration script připravený pro aplikaci (`scripts/migration-script.sql`)

✅ **Dokumentace**
- Backlog s 90+ user stories (165 SP celkem)
- Technický návrh s architekturou
- API contracts s příklady
- Deployment guide pro Azure + AWS

✅ **Konfigurace**
- PostgreSQL DDL se pgvector extension (`scripts/init-db.sql`)
- Python settings.yaml
- Docker Compose pro celý stack
- .gitignore pro .NET a Python
- NuGet balíčky (EF Core, PostgreSQL, pgvector, AutoMapper, MudBlazor)

✅ **Build úspěšný**
- Všechny projekty se kompilují bez chyb ✅

---

## 🚀 Spuštění aplikace (Next Steps)

### Krok 1: Spuštění PostgreSQL databáze

**Pomocí Docker Compose** (doporučené):

```bash
# Spuštění PostgreSQL + Redis
docker-compose up -d postgres

# Kontrola zda je databáze ready
docker ps | grep postgres

# Loggování
docker logs realestate-db
```

**Ručně (lokální PostgreSQL)**:

```bash
# Vytvoř databázi
createdb realestate_dev -U postgres

# Spusť init skript
psql -U postgres -d realestate_dev -f scripts/init-db.sql
```

### Krok 2: Aplikování EF Core migrací

```bash
cd src/RealEstate.Api

# Aplikuj migraci na databázi
export PATH="$PATH:/Users/petrsramek/.dotnet/tools"
dotnet ef database update --project ../RealEstate.Infrastructure

# Nebo přímě spusť SQL script
psql -U postgres -d realestate_dev -f ..//..//scripts/migration-script.sql
```

### Krok 3: Spuštění .NET API

```bash
cd src/RealEstate.Api

# Debug mode
dotnet run

# API bude dostupná na:
# http://localhost:5001 (HTTP)
# Swagger: http://localhost:5001/swagger
```

### Krok 4: Spuštění Blazor aplikace

```bash
cd src/RealEstate.App

# Debug mode
dotnet run

# Aplikace bude na http://localhost:5002
```

### Krok 5: Playwright scraping (REMAX)

```bash
curl -X POST http://localhost:5001/api/scraping-playwright/run \
   -H "Content-Type: application/json" \
   -d '{"sourceCodes":["REMAX"],"remaxProfile":{"regionId":116,"districtId":3713}}'
```

### Krok 6: Python scraper (volitelné)

```bash
cd scraper

# Vytvoř venv
python -m venv venv
source venv/bin/activate

# Instaluj dependencies
pip install -r requirements.txt

# Spusť scraper
python run_api.py
# API bude na http://localhost:8001
```

---

### Krok 7: Ollama + RAG (lokální AI – volitelné)

> Vyžaduje Ollama nainstalovaný lokálně (https://ollama.com). Funguje offline, žádná data neodcházení.

```bash
# 1. Stáhnout modely (jednorázově)
ollama pull nomic-embed-text   # 274 MB – embedding model
ollama pull qwen2.5:14b        # 9 GB  – chat model

# 2. Ujistit se že Ollama běží
ollama serve   # nebo: launchctl start com.ollama.ollama (pokud nainstalováno přes .pkg)

# 3. Ověřit RAG status (API musí běžet)
curl http://localhost:5001/api/rag/status | jq

# Očekávaná odpověď:
# { "provider": "ollama", "isConfigured": true, "embeddingModel": "nomic-embed-text",
#   "chatModel": "qwen2.5:14b", "totalAnalyses": 0, "embeddedAnalyses": 0 }

# 4. Batch-embed popisy všech inzerátů (doporučeno po prvním importu)
curl -X POST http://localhost:5001/api/rag/embed-descriptions \
  -H "Content-Type: application/json" \
  -d '{"limit": 500}'
# Odpověď: { "processed": 482, "message": "Zpracováno 482 inzerátů" }

# 5. Test RAG chatu
curl -X POST http://localhost:5001/api/listings/{listing-uuid}/ask \
  -H "Content-Type: application/json" \
  -d '{"question":"Jaké jsou hlavní výhody této nemovitosti?","topK":5}' | jq
```

**Konfigurace v `appsettings.json`:**
```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "ChatModel": "qwen2.5:14b"
  },
  "Embedding": {
    "Provider": "ollama",
    "VectorDimensions": "768"
  }
}
```

**V Dockeru** (Ollama na host mašině):
```
Ollama__BaseUrl=http://host.docker.internal:11434
```

---

### Krok 8: MCP Server – Claude Desktop integrace (volitelné)

MCP server umožňuje AI asistentům (Claude Desktop) přímo přistupovat k databázi inzerátů.

```bash
# Nainstaluj závislosti
cd mcp
pip install -r requirements.txt

# Test spuštění (stdio pro Claude Desktop)
API_BASE_URL=http://localhost:5001 python server.py
```

**Konfigurace Claude Desktop** (`~/Library/Application Support/Claude/claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "realestate": {
      "command": "python",
      "args": ["/Users/petrsramek/Projects/RealEstateAggregator/mcp/server.py"],
      "env": {
        "API_BASE_URL": "http://localhost:5001"
      }
    }
  }
}
```

Restart Claude Desktop → v nabídce nástrojů uvidíš `realestate` s 9 nástroji.

**Dostupné nástroje:** `search_listings`, `get_listing`, `get_analyses`, `save_analysis`, `ask_listing`, `ask_general`, `list_sources`, `get_rag_status`, `embed_description`, `bulk_embed_descriptions`

---

## 📋 Co je připraveno k použití

### DbContext a Entity Framework
- ✅ `RealEstate.Infrastructure/RealEstateDbContext.cs` - kompletní mapování s pgvector
- ✅ `RealEstate.Infrastructure/Migrations/20260222153038_InitialSchema.cs` - migration ready
- ✅ `RealEstate.Infrastructure/RealEstateDesignTimeDbContextFactory.cs` - design-time factory

### SQL skripty
- ✅ `scripts/init-db.sql` - DDL schema s pgvector, indexes, seed data (3 zdroje)
- ✅ `scripts/migration-script.sql` - EF migration SQL idempotentní script (299 řádků)

### Configuration
- ✅ `appsettings.Development.json` - connection string nakonfigurován
- ✅ `ServiceCollectionExtensions.cs` - DI registration pro DbContext s pgvector support

---

## 🔄 EF Core cheat sheet

```bash
# Vytvoř novou migraci (po změně modelu)
dotnet ef migrations add MigrationName --project ../RealEstate.Infrastructure

# Smaž poslední migraci (pokud jsi ji ještě nespustil)
dotnet ef migrations remove --project ../RealEstate.Infrastructure

# Podívej se co se změní
dotnet ef migrations script --idempotent

# Smaž všechno a začni znovu
dotnet ef database drop --force --project ../RealEstate.Infrastructure
dotnet ef database update --project ../RealEstate.Infrastructure

# Generuj SQL bez aplikování
dotnet ef migrations script --output migrations.sql --idempotent
```

---

## ⚙️ Další kroky (v pořadí priority)

### Sprint 1: MVP (4 týdny)

1. **EF Core & PostgreSQL** ✅ HOTOVO
   - DbContext s pgvector
   - Migrations
   - Seed data

2. **Repositories a Services** (Tý den 1)
   - ListingRepository s filtry (PredicateBuilder pattern)
   - ListingService s DDD pattern

3. **API endpoints** (Týden 2)
   - GET /api/listings - paginated list s filtr
   - POST /api/listings - create (z scraperu)
   - PUT /api/listings/{id} - update
   - GET /api/listings/{id} - detail

4. **Blazor UI** (Týden 3)
   - ListingGrid s MudBlazor DataGrid
   - FilterPanel s MudForm components
   - Pagination a sorting

5. **Python scraper** (Týden 4)
   - DB persistence (z Listing entit)
   - Retry logic a error handling

### Sprint 7: Semantic Search (3 týdny)

1. **EmbeddingService**
   - OpenAI integration (text-embedding-3-small)
   - Batch processing

2. **SemanticSearchService**
   - Vector similarity search s SQL
   - Hybrid classic + semantic filtering

3. **Blazor semantic search UI**
   - Text input pro natural language query
   - Zobrazení similarity scores

---

## 📚 Dokumentace

- [PROJECT_ANALYSIS.md](docs/PROJECT_ANALYSIS.md) - Kompletní analýza projektu (75%)
- [FILTERING_ARCHITECTURE.md](docs/FILTERING_ARCHITECTURE.md) - MudBlazor + PredicateBuilder pattern
- [PGVECTOR_SEMANTIC_SEARCH.md](docs/PGVECTOR_SEMANTIC_SEARCH.md) - Kompletní pgvector guide
- [TECHNICAL_DESIGN.md](docs/TECHNICAL_DESIGN.md) - Architektura a design decisions
- [API_CONTRACTS.md](docs/API_CONTRACTS.md) - API documentation
- [DEPLOYMENT.md](docs/DEPLOYMENT.md) - Deployment procedures

---

## 🐛 Troubleshooting

### PostgreSQL connection failed
```bash
# Zkontroluj jestli běží
docker ps | grep postgres

# Zkontroluj logs
docker logs realestate-db

# Zkontroluj connection string v appsettings.Development.json
```

### Migration failed
```bash
# Smaž starou databázi a začni znovu
docker-compose down -v postgres
docker-compose up -d postgres

# Počkej na health check
sleep 10

# Aplikuj migrate znovu
dotnet ef database update --project ../RealEstate.Infrastructure
```

### Docker auth failed
```bash
# Reset Docker credentials
docker logout
docker login

# Nebo zkus image ze starého tagu
docker pull postgres:15
```

---

## 📝 Co dál?

1. **Spusť PostgreSQL**: `docker-compose up -d postgres`
2. **Aplikuj migrations**: `dotnet ef database update --project src/RealEstate.Infrastructure --startup-project src/RealEstate.Api`
3. **Spusť API**: `cd src/RealEstate.Api && dotnet run`
4. **Jdi do Swaggeru**: http://localhost:5000/swagger
5. **Builduj features** z backlogu!

Máš kompletní technický základ. Zbývá jen implementovat business logiku!
- `Infrastructure/Repositories/ListingRepository.cs` - implementace

### 3. API DTOs & Controllers (Sprint 2)

**Soubory k vytvoření**:
- `Api/DTOs/Listing/*.cs` - všechny DTO třídy  
- `Api/Mapping/MappingProfile.cs` - AutoMapper konfigurace
- `Api/Controllers/ListingsController.cs` - GET /api/listings
- `Api/Controllers/SourcesController.cs` - GET /api/sources
- `Api/Controllers/AnalysisController.cs` - POST /api/listings/{id}/analysis

### 4. Python Scrapers (Sprint 3)

**Soubory k vytvoření**:
- `core/models.py` - Python dataclasses
- `core/db.py` - DB connection management
- `core/runner.py` - orchestrator
- `scrapers/base_scraper.py` - base class/protocol
- `scrapers/remax_scraper.py` - Remax implementace
- `scrapers/mmreality_scraper.py` - MM Reality implementace

### 5. Blazor Frontend (Sprint 5)

**Komponenty k vytvoření**:
- `App/Pages/Dashboard.razor` - hlavní listing stránka
- `App/Components/FilterPanel.razor` - filtrační panel
- `App/Components/ListingDetailDialog.razor` - detail dialog
- `App/Services/ListingApiService.cs` - API client
- `App/Shared/MainLayout.razor` - layout s MudBlazor

---

## 📚 Jak začít vyvíjet

### Prerekvizity

```bash
# .NET SDK
dotnet --version  # Mělo by být 9.0+

# Python
python --version  # Mělo by být 3.12+

# Docker (volitelné, ale doporučené)
docker --version
docker-compose --version
```

### Lokální vývoj

#### Option A: Docker (jednodušší)

```bash
# Spustit pouze PostgreSQL
docker-compose up -d postgres

# Počkat až DB naběhne
docker-compose logs -f postgres

# Ctrl+C pro zastavení sledování logů
```

#### Option B: Lokální PostgreSQL

```bash
# Vytvořit databázi
createdb realestate_dev

# Nebo v psql
psql -U postgres
CREATE DATABASE realestate_dev;
\q
```

### Spustit .NET API

```bash
cd src/RealEstate.Api

# První spuštění - po vytvoření migrací
dotnet ef database update

# Spustit server
dotnet run

# Otevřít browser: https://localhost:5001/swagger
```

### Spustit Python Scraper

```bash
cd scraper

# Vytvořit virtual env
python -m venv venv
source venv/bin/activate  # Mac/Linux
# venv\Scripts\activate  # Windows

# Nainstalovat dependencies
pip install -r requirements.txt

# Spustit jednorázově
python -m core.runner
```

---

## 📖 Užitečné příkazy

### .NET

```bash
# Build celého solution
dotnet build

# Spustit testy
dotnet test

# Přidat NuGet balíček
dotnet add package <PackageName>

# EF Core migrace
dotnet ef migrations add <Name>
dotnet ef database update
```

### Python

```bash
# Aktivovat venv
source venv/bin/activate  # Mac/Linux
venv\Scripts\activate     # Windows

# Instalace dependencies
pip install -r requirements.txt

# Formátování kódu
black .

# Linting
flake8

# Testy
pytest
```

### Docker

```bash
# Spustit celý stack
docker-compose up -d

# Zobrazit logy
docker-compose logs -f [service-name]

# Zastavit stack
docker-compose down

# Rebuild + restart
docker-compose up -d --build
```

---

## 🔍 Kontrola stavu

### Ověřit, že všechno funguje

```bash
# 1. Build projektu
cd ~/Projects/RealEstateAggregator
dotnet build
# ✅ Sestavení úspěšné

# 2. PostgreSQL běží
docker ps | grep postgres
# nebo
psql -h localhost -U postgres -l

# 3. Python dependencies
cd scraper
pip list | grep -E "httpx|beautifulsoup4|asyncpg"

# 4. API běží
curl http://localhost:5001/health
# (po vytvoření health check endpointu)
```

---

## 🐛 Troubleshooting

### Build chyby

```bash
# Vyčistit build artefakty
dotnet clean
rm -rf bin/ obj/

# Rebuild
dotnet restore
dotnet build
```

### Database connection problémy

```bash
# Test connection
psql -h localhost -U postgres -d realestate_dev

# Zkontrolovat connection string
cat src/RealEstate.Api/appsettings.json | grep ConnectionStrings
```

### Python import errors

```bash
# Reinstalovat dependencies
pip install -r requirements.txt --force-reinstall

# Zkontrolovat venv
which python  # Mělo by ukazovat na venv
```

---

## 📊 Aktuální stav projektu

| Komponenta | Status | % Hotovo |
|------------|--------|----------|
| **Projektová struktura** | ✅ Hotovo | 100% |
| **Dokumentace** | ✅ Hotovo | 100% |
| **Doménový model** | ✅ Hotovo | 100% |
| **EF Core DbContext** | ⏳ Připraveno | 0% |
| **Repository Pattern** | ⏳ Připraveno | 0% |
| **API Endpoints** | ⏳ Připraveno | 0% |
| **Python Scrapers** | ⏳ Připraveno | 0% |
| **Blazor Frontend** | ⏳ Připraveno | 0% |
| **Analysis Background** | ⏳ Připraveno | 0% |
| **Cloud Storage** | ⏳ Připraveno | 0% |

**Celkový progres**: ~25% (infrastruktura a návrh hotové)

---

## 🎯 MVP Milestones

### Milestone 1: Database & API (3 týdny)
- [ ] EF Core DbContext + migrace
- [ ] Repository pattern
- [ ] API endpoints (Listings, Sources)
- [ ] Swagger dokumentace funkční

### Milestone 2: Scraping (2 týdny)
- [ ] Python core (db, models, runner)
- [ ] Remax scraper
- [ ] MM Reality scraper
- [ ] Scheduler s cron

### Milestone 3: Frontend (2 týdny)
- [ ] Blazor layout + MudBlazor
- [ ] Dashboard s listingem
- [ ] Filtrační panel
- [ ] Detail dialog

### Milestone 4: AI Analysis (2 týdny)
- [ ] Background služba
- [ ] Google Drive integrace
- [ ] Document generator
- [ ] Frontend UI pro analýzu

**Celkem: ~9 týdnů do MVP** 🚀

---

## 📞 Další kroky

1. **Začít s implementací** podle backlogu ([BACKLOG.md](docs/BACKLOG.md))
2. **Přečíst technický návrh** pro detaily ([TECHNICAL_DESIGN.md](docs/TECHNICAL_DESIGN.md))
3. **Vytvořit GitHubrepo** a nahrát kód
4. **Setupnout dev prostředí** (PostgreSQL + .NET + Python)
5. **Začít s US-102**: Vytvořit EF Core DbContext

---

**Pokud máš jakékoliv otázky k projektu, mrkni do dokumentace v `docs/` nebo se ptej!** 🎉

**Happy coding!** 💻
