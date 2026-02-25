# Real Estate Aggregator

> **Komplexní agregátor realitních inzerátů s pokročilým filtrováním, AI analýzou a lokálním RAG**  
> *.NET 10 • MudBlazor 9 • pgvector • Ollama • Python Scraping • MCP*

---

## 📋 Přehled projektu

Real Estate Aggregator je systém pro automatický sběr, normalizaci a správu realitních inzerátů z 12 českých zdrojů. Podporuje centralizované vyhledávání, filtrování, AI chat nad inzeráty (RAG), export do cloudu a integraci s Claude Desktop přes MCP.

**Aktuální stav:** ~1 230 aktivních inzerátů, 12 zdrojů, Docker stack plně funkční

### Klíčové funkce

✅ **Automatický scraping** – 12 zdrojů (SReality, IDNES, REMAX, Century21, MMR, Premiera Reality aj.)  
✅ **Jednotný datový model** – normalizace PropertyType/OfferType včetně dražeb (Auction)  
✅ **Pokročilé filtrování** – typ, nabídka, cena, lokalita, fulltextový GIN index  
✅ **RAG + AI chat** – lokální Ollama (nomic-embed-text + qwen2.5:14b), pgvector 768 dim  
✅ **MCP server** – 9 nástrojů pro Claude Desktop / AI asistenty  
✅ **Cloud export s retry** – Google Drive + OneDrive, retry 3×, foto stats v UI  
✅ **User management** – označování (líbí/nelíbí/navštívit), poznámky, favority  
✅ **Moderní UI** – Blazor + MudBlazor 9, responzivní, filter state persistence  

---

## 🏗️ Architektura

```
┌──────────────────────────────────────────────────────────────┐
│                  Frontend (Blazor + MudBlazor)                │
│  • Listingy s filtry  • Detail + RAG chat  • Cloud export    │
└──────────────────────┬───────────────────────────────────────┘
                       │ REST API
┌──────────────────────▼───────────────────────────────────────┐
│            Backend (.NET 10 - ASP.NET Core Minimal APIs)      │
│  • ListingService  • RagService  • ExportService (GD/OD)     │
└──────────────────────┬───────────────────────────────────────┘
                       │
     ┌─────────────────┼────────────────────┐
     │                 │                    │
┌────▼──────┐  ┌───────▼──────┐  ┌─────────▼──────────┐
│ PostgreSQL│  │ Cloud Storage│  │ Ollama :11434       │
│ +pgvector │  │ Google Drive │  │ nomic-embed-text    │
│ 12 zdrojů │  │ OneDrive     │  │ qwen2.5:14b         │
│ ~1 230 inz│  │ (retry 3x)   │  │ (lokální, offline)  │
└───────────┘  └──────────────┘  └────────────────────-┘
     ▲
┌────┴──────────────────────────────────────────────────────┐
│    Scraping (Playwright .NET + Python FastAPI :8001)       │
│  12 zdrojů: SReality, IDNES, REMAX, C21, MMR, Premiera..  │
└────────────────────────────────────────────────────────────┘
          ▲
┌─────────┴─────────────────────────────────────────────────┐
│              MCP Server (Python FastMCP :8002)             │
│  9 nástrojů – stdio (Claude Desktop) + SSE (Docker)       │
└────────────────────────────────────────────────────────────┘
```

---

## 🛠️ Technologický stack

### Backend (.NET 10)
- **Framework**: ASP.NET Core 10.0 Minimal APIs
- **UI**: Blazor Web App + MudBlazor 9.x
- **ORM**: Entity Framework Core 10 + EFCore.NamingConventions
- **Databáze**: PostgreSQL 15 + pgvector (768-dim embeddingy)
- **AI**: Ollama (nomic-embed-text + qwen2.5:14b) / OpenAI (fallback)
- **API integrace**: Google Drive API, Microsoft Graph API (OneDrive)
- **Security**: API key middleware, CORS, CancellationToken pattern

### Scraping (.NET + Python)
- **Primary**: Python FastAPI :8001 (12 scraperů s retry logic)
- **Playwright**: .NET scraper pro REMAX
- **HTTP**: `httpx` + tenacity retry decorator
- **Parsing**: `BeautifulSoup4` + regex selektory
- **DB**: `asyncpg` pool, upsert pattern, max 20 fotek
- **Deaktivace**: `deactivate_unseen_listings()` po `full_rescan`

### AI & MCP
- **Embeddingy**: Ollama `nomic-embed-text` (768 dim, lokální, offline)
- **Chat**: Ollama `qwen2.5:14b` (lokální, ~9 GB, M2 Ultra)
- **Vektorová DB**: pgvector IVFFlat index (cosine distance)
- **MCP Server**: FastMCP 3.x, 9 nástrojů, stdio + SSE transport

### Infrastruktura
- **Hosting**: Docker Compose (5 služeb: postgres, api, app, scraper, mcp)
- **Restart policy**: `unless-stopped` na všech službách
- **Storage**: Google Drive / OneDrive (export s retry 3×)
- **CI/CD**: GitHub Actions (planned)

---

## 📁 Struktura projektu

```
RealEstateAggregator/
├── src/
│   ├── RealEstate.Api/              # ASP.NET Core Web API
│   ├── RealEstate.App/              # Blazor frontend (MudBlazor)
│   ├── RealEstate.Domain/           # Doménové modely, enums, rozhraní
│   ├── RealEstate.Infrastructure/   # EF Core, repositories, cloud integrace
│   └── RealEstate.Background/       # Background služby (AnalysisJob)
│
├── tests/
│   └── RealEstate.Tests/            # Unit + integration testy
│
├── scraper/                         # Python scraping projekt
│   ├── scrapers/                    # Implementace scraperů pro jednotlivé RK
│   │   ├── remax_scraper.py
│   │   ├── mmreality_scraper.py
│   │   └── prodejme_to_scraper.py
│   ├── core/                        # Společná logika
│   │   ├── models.py
│   │   ├── db.py
│   │   └── runner.py
│   └── config/
│       └── settings.yaml
│
├── docs/                            # Dokumentace
│   ├── BACKLOG.md                   # Product backlog
│   ├── TECHNICAL_DESIGN.md          # Technický návrh
│   ├── API_CONTRACTS.md             # API dokumentace
│   └── DEPLOYMENT.md                # Deployment guide
│
├── RealEstateAggregator.sln         # .NET solution
└── README.md                        # Tento soubor
```

---

## 🚀 Rychlý start

### Požadavky
- .NET 10 SDK
- Python 3.12+
- PostgreSQL 15+
- Node.js 20+ (pro Blazor dev tools)

### 1. Databáze
```bash
# Spustit PostgreSQL
docker run --name realestate-db -e POSTGRES_PASSWORD=dev -p 5432:5432 -d postgres:15

# Vytvořit databázi
psql -h localhost -U postgres -c "CREATE DATABASE realestate_dev;"
```

### 2. Backend (.NET)
```bash
cd src/RealEstate.Api
dotnet restore
dotnet ef database update
dotnet run
```
Backend běží na `http://localhost:5001`

### 3. Scraper (Python)
```bash
cd scraper
python -m venv venv
source venv/bin/activate  # Windows: venv\Scripts\activate
pip install -r requirements.txt
python -m core.runner
```

### 4. Frontend
Frontend běží jako samostatná Blazor App na `http://localhost:5002`

### 5. Playwright scraping (REMAX)
```bash
curl -X POST http://localhost:5001/api/scraping-playwright/run \
   -H "Content-Type: application/json" \
   -d '{"sourceCodes":["REMAX"],"remaxProfile":{"regionId":116,"districtId":3713}}'
```

---

## 📊 Datový model (jádro)

### Source
Zdroj inzerátů (realitní kancelář)
- `Id`, `Name`, `BaseUrl`, `IsActive`

### Listing
Normalizovaný inzerát
- Základní info: `Title`, `Description`, `Url`, `ExternalId`
- Kategorizace: `PropertyType`, `OfferType`
- Cena: `Price`, `PriceNote`
- Lokace: `LocationText`, `Region`, `District`, `Municipality`
- Parametry: `AreaBuiltUp`, `AreaLand`, `Rooms`, `ConstructionType`, `Condition`
- Metadata: `FirstSeenAt`, `LastSeenAt`, `IsActive`

### ListingPhoto
Fotografie inzerátu
- `ListingId`, `OriginalUrl`, `StoredUrl`, `Order`

### UserListingState
Stav inzerátu per uživatel
- `UserId`, `ListingId`, `Status` (New/Liked/Disliked/Ignored/ToVisit/Visited)
- `Notes`, `LastUpdated`

### AnalysisJob
AI analýza inzerátu
- `ListingId`, `Status`, `StorageProvider`, `StoragePath`
- `RequestedAt`, `FinishedAt`, `ErrorMessage`

---

## 🎯 API Endpoints (přehled)

### Listings
- `POST /api/listings/search` – seznam s filtrací a paginací
- `GET /api/listings/{id}` – detail inzerátu
- `POST /api/listings/{id}/state` – uložit user stav

### Sources
- `GET /api/sources` – seznam realitních kanceláří

### Analysis / Export
- `POST /api/listings/{id}/analysis` – spustit AI analýzu (export GD/OD)
- `POST /api/listings/{id}/export/drive` – export na Google Drive
- `POST /api/listings/{id}/export/onedrive` – export na OneDrive

### RAG (Retrieval-Augmented Generation)
- `POST /api/listings/{id}/analyses` – uložit analýzu + vytvořit embedding
- `GET /api/listings/{id}/analyses` – seznam analýz inzerátu
- `DELETE /api/listings/{id}/analyses/{aId}` – smazat analýzu
- `POST /api/listings/{id}/ask` – AI chat pro jeden inzerát
- `POST /api/rag/ask` – AI chat napříč všemi inzeráty
- `GET /api/rag/status` – stav RAG (provider, počty)
- `POST /api/listings/{id}/embed-description` – auto-embed popisu (idempotentní)
- `POST /api/rag/embed-descriptions` – batch embed všech inzerátů

### Scraping (chráněno API klíčem `X-Api-Key`)
- `POST /api/scraping/trigger` – spustit scraping (přes Python API)

---

## 📚 Dokumentace
- [docs/TECHNICAL_DESIGN.md](docs/TECHNICAL_DESIGN.md) – technický návrh + RAG architektura
- [docs/API_CONTRACTS.md](docs/API_CONTRACTS.md) – API dokumentace
- [docs/RAG_MCP_DESIGN.md](docs/RAG_MCP_DESIGN.md) – detailní design RAG + MCP serveru
- [docs/AI_SESSION_SUMMARY.md](docs/AI_SESSION_SUMMARY.md) – historie sessions + changelog
- [docs/BACKLOG.md](docs/BACKLOG.md) – backlog a known issues

---

## 🔄 Workflow scrapingu

1. **Periodický job** (cron/timer) spustí runner
2. **Runner** projde všechny aktivní scrapers
3. Pro každý scraper:
   - Fetch listings (paginace přes listing stránky)
   - Fetch detail (HTML/JSON detailu)
   - Normalize (parsování → strukturovaná data)
4. **Upsert do DB**:
   - Nový inzerát → insert + `FirstSeenAt`
   - Existující → update ceny/parametrů + `LastSeenAt`
5. **Deaktivace** – inzeráty neviděné X běhů → `IsActive = false`

---

## 🧠 Funkce "Udělej analýzu"

Vytvoří balíček pro AI zpracování:

1. Uživatel klikne "Udělej analýzu" na inzerátu
2. Backend vytvoří `AnalysisJob` (status: Pending)
3. Background service:
   - Stáhne listing data + fotky
   - Vygeneruje dokument (Markdown/HTML/Word):
     - Tabulka parametrů
     - Originální text
     - Seznam fotek
   - Nahraje na Google Drive / OneDrive
4. `AnalysisJob.Status` → Succeeded, uložen link
5. Frontend zobrazí tlačítko "Otevřít v Drive"

---

## 📈 Roadmap

### ✅ v1.0 (Sessions 1–5, únor 2026)
- [x] 12 scraperů (SReality, IDNES, REMAX, Century21, MMR, Premiera Reality aj.)
- [x] .NET 10 backend s EF Core + pgvector
- [x] Blazor frontend s MudBlazor 9
- [x] Filtrování, user stavy, filter state persistence
- [x] Cloud export (Google Drive + OneDrive)
- [x] Docker stack (5 služeb, restart: unless-stopped)
- [x] OfferType.Auction + SReality dražby
- [x] Fulltext GIN index, tiebreaker, CORS, API key security
- [x] 39 unit testů

### ✅ v1.1 (Session 6, 25. února 2026)
- [x] RAG lokální AI (pgvector + Ollama, 768 dim)
- [x] AI chat nad inzerátem (ListingDetail.razor)
- [x] Batch embedding (auto-embed popisu inzerátu)
- [x] MCP server (9 nástrojů, Claude Desktop integrace)
- [x] Cloud export retry 3× + foto stats badge v UI

### Plánováno
- [ ] Photo download pipeline (original_url → stored_url, S3/lokální)
- [ ] HNSW index (pro > 10k vektorů)
- [ ] Hybrid search (BM25 tsvector + cosine similarity)
- [ ] Mapové zobrazení inzerátů (PostGIS / Leaflet)
- [ ] Prostorové filtrování – koridor kolem trasy (ST_Buffer, RÚIAN)
- [ ] Autentizace/autorizace (ASP.NET Identity)
- [ ] Background scheduled scraping (APScheduler / Hangfire)

---

## 📝 Licence

Tento projekt je privátní. Všechna práva vyhrazena.

---

## 🤝 Kontakt

Pro otázky a podporu kontaktujte vlastníka projektu.

**Vytvořeno**: Únor 2026  
**Verze**: 1.1.0 (25. února 2026 – RAG + MCP + Export retry)
