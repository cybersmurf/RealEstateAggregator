# Real Estate Aggregator

> **Komplexní agregátor realitních inzerátů s pokročilým filtrováním, AI analýzou a lokálním RAG**  
> *.NET 10 • MudBlazor 9 • PostGIS + pgvector • Ollama • Python Scraping • MCP*

---

## 📋 Přehled projektu

Real Estate Aggregator je systém pro automatický sběr, normalizaci a správu realitních inzerátů ze 14 českých zdrojů. Podporuje centralizované vyhledávání, filtrování, AI chat nad inzeráty (RAG), prostorové analýzy (PostGIS), export do cloudu a integraci s Claude Desktop přes MCP.

**Aktuální stav:** ~1 558 aktivních inzerátů · 14 zdrojů · 97 % GPS pokrytí · AI enrichment 91 % · Docker stack plně funkční

### Klíčové funkce

✅ **Automatický scraping** – 14 zdrojů (SReality, IDNES, REMAX, Century21, MMR, Premiera Reality, Bazoš aj.)  
✅ **Jednotný datový model** – normalizace PropertyType/OfferType včetně dražeb (Auction)  
✅ **Pokročilé filtrování** – typ, nabídka, cena, lokalita, fulltextový GIN index  
✅ **RAG + AI chat** – lokální Ollama (nomic-embed-text + qwen2.5:14b), pgvector 768 dim  
✅ **MCP server** – 14 nástrojů pro Claude Desktop / AI asistenty  
✅ **Cloud export s retry** – Google Drive + OneDrive, retry 3×, foto stats v UI  
✅ **User management** – označování (líbí/nelíbí/navštívit), poznámky, favority  
✅ **Moderní UI** – Blazor + MudBlazor 9, responzivní, filter state persistence  
✅ **Prostorové analýzy** – PostGIS 3.4, Leaflet mapa, koridor podél trasy (OSRM + ST_Buffer)  
✅ **Katastr nemovitostí** – ČÚZK/RUIAN integrace, KN OCR přes llama3.2-vision  
✅ **AI enrichment** – SmartTags, PriceSignal, normalizace dat via Ollama bulk joby  
✅ **Price history** – sledování změn cen, trend vizualizace v detailu inzerátu  
✅ **Monitoring** – Slack alerting, `/v1/health/scrapers` endpoint, APScheduler (3 AM cron)  

---

## 🏗️ Architektura

```
┌──────────────────────────────────────────────────────────────────────┐
│             Frontend (Blazor + MudBlazor 9) :5002                    │
│  • Listingy + filtry  • Detail + RAG chat  • Mapa (Leaflet+PostGIS) │
│  • Katastr KN OCR     • Cloud export       • Moje inzeráty          │
└──────────────────────────┬───────────────────────────────────────────┘
                           │ REST API
┌──────────────────────────▼───────────────────────────────────────────┐
│         Backend (.NET 10 – ASP.NET Core Minimal APIs) :5001          │
│  • ListingService  • RagService  • SpatialService  • CadastreService │
│  • OllamaTextService (SmartTags/PriceSignal/Normalize)               │
│  • PhotoClassificationService  • ExportService (GD/OD)               │
└──────┬────────────────────────────────────────────────────────────────┘
       │
  ┌────┴──────────────┬──────────────────┬──────────────────┐
  │                   │                  │                  │
┌─▼──────────┐  ┌─────▼──────┐  ┌───────▼──────┐  ┌───────▼──────────┐
│ PostgreSQL │  │Cloud Storage│  │ Ollama :11434│  │ Python Scraper   │
│ PostGIS 3.4│  │Google Drive │  │nomic-embed   │  │ FastAPI :8001    │
│ + pgvector │  │OneDrive     │  │qwen2.5:14b   │  │ 14 zdrojů        │
│ ~1 558 inz │  │(retry 3×)   │  │llama3.2-vis  │  │ APScheduler 3 AM │
└────────────┘  └────────────┘  └──────────────┘  │ Slack alerting   │
                                                   └──────────────────┘
┌──────────────────────────────────────────────────────────────────────┐
│         MCP Server (Python FastMCP 3.x) :8002                        │
│  14 nástrojů – stdio (Claude Desktop) + SSE (Docker)                │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 🛠️ Technologický stack

### Backend (.NET 10)
- **Framework**: ASP.NET Core 10.0 Minimal APIs
- **UI**: Blazor Web App + MudBlazor 9.x
- **ORM**: Entity Framework Core 10 + EFCore.NamingConventions (snake_case)
- **Databáze**: PostgreSQL 15 + PostGIS 3.4 + pgvector (768-dim embeddingy)
- **AI enrichment**: Ollama (nomic-embed-text · qwen2.5:14b · llama3.2-vision:11b)
- **API integrace**: Google Drive API, Microsoft Graph API (OneDrive)
- **Security**: API key middleware, CORS, CancellationToken pattern, porty bind na 127.0.0.1

### Scraping (Python FastAPI)
- **14 scraperů**: SReality, IDNES, REMAX, C21, MMR, Premiera, Delux, HV, Lexamo, ZnojmoReality, NemZnojmo, REAS, Bazoš, ProdejmeTo
- **HTTP**: `httpx` + tenacity retry decorator (429/5xx only)
- **Parsing**: `BeautifulSoup4` + regex selektory
- **DB**: `asyncpg` pool, upsert pattern, max 20 fotek per listing
- **Deaktivace**: `deactivate_unseen_listings()` po `full_rescan`
- **Scheduling**: APScheduler – denní cron 3:00 + weekly full_rescan (neděle 2:00)
- **Monitoring**: Slack webhook alerting, `/v1/health/scrapers`

### AI & MCP
- **Embeddingy**: Ollama `nomic-embed-text` (768 dim, lokální, offline)
- **Chat**: Ollama `qwen2.5:14b` (lokální, M2 Ultra)
- **Vision**: Ollama `llama3.2-vision:11b` – foto klasifikace (13 kategorií) + KN OCR
- **Vektorová DB**: pgvector IVFFlat index (cosine distance)
- **AI enrichment**: SmartTags · PriceSignal · Normalizace dat (bulk Ollama joby)
- **MCP Server**: FastMCP 3.x, **14 nástrojů** (search, get_listing, analyses, vision, RAG, ...)

### Prostorové analýzy (PostGIS)
- **Geocoding**: Nominatim, 97 % pokrytí (1 522+ bodů)
- **Koridor**: OSRM routování + `ST_Buffer` (EPSG:5514), Leaflet vizualizace
- **Katastr**: ČÚZK/RUIAN lookup, KN OCR screenshot přes Vision LLM

### Infrastruktura
- **Hosting**: Docker Compose (**6 služeb**: postgres, api, app, scraper, mcp, pgadmin)
- **Restart policy**: `unless-stopped` na všech 5 produkčních službách
- **Storage**: Google Drive / OneDrive (export s retry 3×) + lokální `uploads_data` volume
- **Logging**: Serilog structured logging (CompactJsonFormatter v produkci)

---

## 📁 Struktura projektu

```
RealEstateAggregator/
├── src/
│   ├── RealEstate.Api/              # ASP.NET Core Minimal APIs + Services
│   │   ├── Endpoints/               # 15 endpoint skupin (Listings, RAG, Spatial, Cadastre, ...)
│   │   ├── Services/                # 30+ service tříd + interfaces
│   │   └── Templates/               # AI instrukce šablony (editovatelné bez recompilace)
│   ├── RealEstate.App/              # Blazor frontend (MudBlazor 9)
│   │   └── Components/Pages/        # Listings, ListingDetail, Map, MyListings, Scrape, DecisionReport
│   ├── RealEstate.Domain/           # Doménové modely, enums, interfaces
│   ├── RealEstate.Infrastructure/   # EF Core DbContext, Migrations, Repositories
│   ├── RealEstate.Export/           # Export content builders (Markdown, Word)
│   └── RealEstate.Background/       # Background jobs (AnalysisJob)
│
├── tests/
│   └── RealEstate.Tests/            # 79 xUnit testů (Cadastre, ExportBuilder, Rag, Unit)
│
├── scraper/                         # Python FastAPI scraping service
│   ├── core/scrapers/               # 14 scraperů (remax, sreality, bazos, ...)
│   ├── core/runner.py               # Orchestrátor + APScheduler
│   ├── core/filters.py              # FilterManager (geo, quality, price)
│   ├── core/notifications.py        # Slack alerting
│   └── tests/                       # 97 pytest testů
│
├── mcp/
│   └── server.py                    # FastMCP 3.x MCP server (14 nástrojů)
│
├── scripts/                         # DB migrace (.sql) + utility skripty
│   ├── init-db.sql                  # Inicializace schématu
│   ├── migrate_price_history.sql    # Historie cen
│   ├── migrate_postgis.sql          # PostGIS spatial tables
│   └── migrate_cadastre.sql         # ČÚZK/RUIAN tabulky
│
├── docs/                            # Dokumentace
├── docker-compose.yml               # 6 služeb (postgres, api, app, scraper, mcp, pgadmin)
├── Makefile                         # make up/down/rebuild-*/logs-*/db/scrape
├── CLAUDE.md                        # Instrukce pro Claude Code
├── AGENTS.md                        # Instrukce pro AI coding agenty
└── README.md                        # Tento soubor
```

---

## 🚀 Rychlý start

### Požadavky
- Docker Desktop / Colima (Apple Silicon: `platform: linux/arm64/v8`)
- Ollama s modely: `nomic-embed-text`, `qwen2.5:14b`, `llama3.2-vision:11b`

### Spuštění (Docker Compose)

```bash
# Start full stack (postgres + api + app + scraper + mcp + pgadmin)
make up

# Zkontrolovat zdraví služeb
make status
```

Služby:
- **Blazor App**: http://localhost:5002
- **API**: http://localhost:5001
- **Scraper API**: http://localhost:8001
- **MCP Server**: http://localhost:8002
- **pgAdmin**: http://localhost:5050

### Ruční scraping

```bash
# Inkrementální (přidá nové inzeráty)
make scrape

# Nebo přímo přes API (X-Api-Key povinný)
curl -X POST http://localhost:5001/api/scraping/trigger \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key-change-me" \
  -d '{"sourceCodes":["SREALITY","REMAX"],"fullRescan":false}'
```

### Lokální vývoj (.NET)

```bash
dotnet build
dotnet test tests/RealEstate.Tests
dotnet run --project src/RealEstate.Api --urls "http://localhost:5001"
dotnet run --project src/RealEstate.App --urls "http://localhost:5002"
```

### Lokální vývoj (Python scraper)

```bash
cd scraper
# Pokud venv neexistuje:
# python3 -m venv .venv && source .venv/bin/activate && pip install -r requirements.txt
source .venv/bin/activate
python run_api.py
```

---

## 📊 Datový model (jádro)

### Source
Zdroj inzerátů (realitní kancelář)
- `Id`, `Code`, `Name`, `BaseUrl`, `IsActive`

### Listing
Normalizovaný inzerát – jádro systému
- Základní info: `Title`, `Description`, `Url`, `ExternalId`, `SourceCode`
- Kategorizace: `PropertyType`, `OfferType` (enum: Sale/Rent/Auction)
- Cena: `Price`, `PriceNote`, `PriceSignal`, `PriceSignalReason`
- Lokace: `LocationText`, `Region`, `District`, `Municipality`, `Latitude`, `Longitude`
- Parametry: `AreaBuiltUp`, `AreaLand`, `Rooms`, `Disposition`, `ConstructionType`, `Condition`
- AI enrichment: `SmartTags`, `DescriptionEmbedding` (pgvector 768 dim)
- Statistiky: `ViewCount`, `DateCreatedSource`
- Metadata: `FirstSeenAt`, `LastSeenAt`, `IsActive`

### ListingPhoto
Fotografie inzerátu – max 20 fotek
- `ListingId`, `OriginalUrl`, `StoredUrl`, `Order`

### ListingPriceHistory
Historie cen – sledování změn
- `ListingId`, `Price`, `RecordedAt`, `Source`

### ListingCadastreData
Data z katastru nemovitostí
- `ListingId`, `ParcelNumber`, `LvNumber`, `LandAreaM2`, `LandType`, `Municipality`
- `Encumbrances`, `FetchStatus` (ruian/ocr)

### UserListingState
Stav inzerátu per uživatel
- `UserId`, `ListingId`, `Status` (New/Liked/Disliked/Ignored/ToVisit/Visited)
- `Notes`, `LastUpdated`

### UserListingPhoto
Fotky z vlastní prohlídky
- `UserId`, `ListingId`, `FilePath`, `Caption`, `Category`

### SpatialArea
Uložená prostorová oblast (koridor, polygon)
- `Name`, `GeometryWkt`, `StartAddress`, `EndAddress`, `BufferMeters`

### AnalysisJob
AI analýza inzerátu
- `ListingId`, `Status`, `StorageProvider`, `StoragePath`
- `RequestedAt`, `FinishedAt`, `ErrorMessage`

---

## 🎯 API Endpoints (přehled)

### Listings
- `POST /api/listings/search` – seznam s filtrací a paginací
- `GET /api/listings/{id}` – detail inzerátu
- `GET /api/listings/{id}/price-history` – historie cen
- `POST /api/listings/{id}/state` – uložit user stav
- `GET /api/listings/export.csv` – CSV export (UTF-8 BOM)
- `GET /api/listings/my-listings` – inzeráty se stavem (seskupené)
- `POST /api/listings/deactivate-dead` – HTTP HEAD check + deaktivace

### Sources
- `GET /api/sources` – seznam realitních kanceláří

### AI Enrichment (Ollama)
- `POST /api/ollama/bulk-normalize` – normalizace dat
- `POST /api/ollama/bulk-smart-tags` – generování smart tagů
- `POST /api/ollama/bulk-price-opinion` – odhad ceny
- `GET /api/ollama/stats` – statistiky AI zpracování

### RAG (Retrieval-Augmented Generation)
- `POST /api/listings/{id}/analyses` – uložit analýzu + embedding
- `GET /api/listings/{id}/analyses` – seznam analýz
- `POST /api/listings/{id}/ask` – AI chat pro jeden inzerát
- `POST /api/rag/ask` – AI chat napříč všemi inzeráty
- `GET /api/rag/status` – stav RAG (provider, počty)
- `POST /api/rag/embed-descriptions` – batch embed popisů

### Spatial (PostGIS)
- `POST /api/spatial/bulk-geocode` – Nominatim geocoding
- `POST /api/spatial/corridor` – OSRM + ST_Buffer koridor
- `GET /api/spatial/map-points` – body pro Leaflet mapu

### Cadastre (ČÚZK/RUIAN)
- `POST /api/cadastre/listings/{id}/fetch` – RUIAN lookup
- `POST /api/cadastre/bulk-fetch` – hromadný RUIAN lookup
- `POST /api/cadastre/listings/{id}/ocr-screenshot` – KN OCR (llama3.2-vision)

### Photos
- `POST /api/photos/bulk-download` – stáhnout fotky locally
- `GET /api/photos/stats` – statistiky stažených fotek
- `POST /api/photos/bulk-classify-inspection` – Vision klasifikace

### Export (Google Drive / OneDrive)
- `POST /api/listings/{id}/export/drive`
- `POST /api/listings/{id}/export/onedrive`

### Scraping (chráněno `X-Api-Key`)
- `POST /api/scraping/trigger` – spustit scraping přes Python API
- `POST /api/scraping-playwright/run` – Playwright scraping (REMAX)

---

## 🧠 MCP Server (Claude Desktop integrace)

14 nástrojů přes FastMCP 3.x (`mcp/server.py`):

| Tool | Popis |
|---|---|
| `search_listings` | Vyhledat inzeráty (text + filtry) |
| `get_listing` | Kompletní detail + ZÁPIS Z PROHLÍDKY + Drive URL |
| `get_inspection_photos` | Fotky z prohlídky |
| `get_listing_photos` | Fotky z inzerátu |
| `analyze_inspection_photos` | Vision analýza fotek z prohlídky |
| `analyze_listing_photos` | Vision analýza fotek inzerátu |
| `analyze_tovisit_listings` | Analýza inzerátů k návštěvě |
| `get_analyses` | Všechny uložené analýzy (plný obsah) |
| `save_analysis` | Uložit analýzu + auto-embedding |
| `ask_listing` | RAG chat pro jeden inzerát |
| `ask_general` | RAG chat přes všechny inzeráty |
| `list_sources` | Přehled aktivních zdrojů |
| `get_rag_status` | Stav RAG (embeddingy, provider) |
| `embed_description` | Embed popisu inzerátu |

**Konfigurace** (`~/Library/Application Support/Claude/claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "realestate": {
      "command": "python3",
      "args": ["/path/to/RealEstateAggregator/mcp/server.py"],
      "env": { "REALESTATE_API_URL": "http://localhost:5001" }
    }
  }
}
```

---

## 🔄 Workflow scrapingu

1. **APScheduler** spustí scraping denně v 3:00 (weekly full_rescan neděle 2:00)
2. **Runner** projde všechny aktivní scrapers (14 zdrojů)
3. Pro každý scraper:
   - Fetch listings (paginace přes listing stránky)
   - Fetch detail (HTML/JSON detailu)
   - Normalize (parsování → strukturovaná data, max 20 fotek)
4. **Upsert do DB**:
   - Nový inzerát → insert + `FirstSeenAt`
   - Existující → update + log cenové změny do `listing_price_history`
5. **Deaktivace** – `deactivate_unseen_listings()` po `full_rescan`
6. **Monitoring** – chyby notifikovány přes Slack webhook

---

## 🗺️ Prostorové analýzy (PostGIS)

- **Geocoding**: Nominatim, 97 % pokrytí (1 522+ GPS bodů)
- **Leaflet mapa**: barevné markery dle typu/nabídky, popup s fotkou + cenou + linkem
- **Koridor trasy**: OSRM routování + `ST_Buffer` (EPSG:5514), uložitelné koridory
- **Spatial filtering**: `ST_Intersects` – inzeráty uvnitř oblasti/koridoru

---

## 🧠 AI Enrichment Pipeline

Tři bulk Ollama joby zpracovávají inzeráty na pozadí:

1. **Normalize** (`/api/ollama/bulk-normalize`) – opravuje překlepy, standardizuje lokality → 91 % pokrytí
2. **SmartTags** (`/api/ollama/bulk-smart-tags`) – generuje štítky (dřevostavba, sklep, garáž…) → 91 % pokrytí
3. **PriceSignal** (`/api/ollama/bulk-price-opinion`) – odhad ceny vs. trh (přeceněno/podceněno/OK) → 79 % pokrytí

**Photo Classification**: `llama3.2-vision:11b` klasifikuje fotky do 13 kategorií (interiér/exteriér/kuchyň/koupelna/…)

**KN OCR**: Screenshot z KN nahlížení → OCR via Vision LLM → extrakce parcelní číslo, LV, výměra, věcná břemena

---

## 📈 Roadmap

### ✅ v1.0 – Základy (Sessions 1–5, únor 2026)
- [x] 12 scraperů (SReality, IDNES, REMAX, Century21, MMR, Premiera Reality aj.)
- [x] .NET 10 backend s EF Core + pgvector
- [x] Blazor frontend s MudBlazor 9
- [x] Filtrování, user stavy, filter state persistence
- [x] Cloud export (Google Drive + OneDrive)
- [x] Docker stack (5 služeb, restart: unless-stopped)
- [x] OfferType.Auction + SReality dražby
- [x] Fulltext GIN index, CORS, API key security
- [x] 39 unit testů

### ✅ v1.1 – RAG + AI (Sessions 6–14, únor 2026)
- [x] RAG lokální AI (pgvector + Ollama, 768 dim)
- [x] AI chat nad inzerátem (ListingDetail.razor)
- [x] MCP server (14 nástrojů, Claude Desktop integrace)
- [x] KN OCR (llama3.2-vision)
- [x] Background scheduled scraping (APScheduler)
- [x] Photo download pipeline + klasifikace

### ✅ v1.2 – Spatial + Enrichment (Sessions 15–22, únor–březen 2026)
- [x] PostGIS 3.4 + Leaflet mapa + koridor trasy
- [x] Nominatim geocoding (97 % pokrytí)
- [x] ČÚZK/RUIAN integrace
- [x] SmartTags, PriceSignal, bulk Normalize (Ollama)
- [x] Serilog structured logging
- [x] CSV export
- [x] Moje inzeráty stránka
- [x] 141 C# testů + 97 Python testů

### ✅ v1.3 – Price History + Monitoring (Sessions 23–28+, kveten 2026)
- [x] **Price history tracking** – listing_price_history tabulka + trend v UI
- [x] **ViewCount + DateCreatedSource** – SReality statistiky
- [x] **SReality v1 API fix** – migrace z deprecated v2 API
- [x] **ProdejmeTo rewrite** – Next.js Server Action API
- [x] **Slack alerting** – webhook notifikace + /v1/health/scrapers
- [x] **Deactivate-dead endpoint** – HTTP HEAD check
- [x] **Docker security** – porty bind na 127.0.0.1
- [x] **Retry fix** – retry pouze 429/5xx, ne 4xx
- [x] 14 scraperů (přidán BAZOS)
- [x] pgAdmin v Docker Compose

### Plánováno
- [ ] HNSW index (pro > 10k vektorů)
- [ ] Hybrid search (BM25 tsvector + cosine similarity)
- [ ] Autentizace/autorizace (ASP.NET Identity)
- [ ] GitHub Actions CI/CD

---

## 📚 Dokumentace
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) – kompletní architektura (Mermaid diagramy, ERD, RAG matematika)
- [docs/TECHNICAL_DESIGN.md](docs/TECHNICAL_DESIGN.md) – technický návrh + RAG architektura
- [docs/API_CONTRACTS.md](docs/API_CONTRACTS.md) – API dokumentace
- [docs/RAG_MCP_DESIGN.md](docs/RAG_MCP_DESIGN.md) – detailní design RAG + MCP serveru
- [docs/RAG_UI_DESIGN.md](docs/RAG_UI_DESIGN.md) – UI standardy pro RAG chat (MudBlazor 9)
- [docs/AI_SESSION_SUMMARY.md](docs/AI_SESSION_SUMMARY.md) – historie sessions + changelog
- [docs/BACKLOG.md](docs/BACKLOG.md) – backlog a known issues
- [AGENTS.md](AGENTS.md) – instrukce pro AI coding agenty
- [CLAUDE.md](CLAUDE.md) – instrukce pro Claude Code

---

---

## 📝 Licence

Tento projekt je privátní. Všechna práva vyhrazena.

---

## 🤝 Kontakt

Pro otázky a podporu kontaktujte vlastníka projektu.

**Vytvořeno**: Únor 2026  
**Verze**: 1.3.0 (25. května 2026 – Price History + Monitoring + 14 scraperů)  
**DB stav**: ~1 558 inzerátů · 14 zdrojů · GPS 97 % · AI 91 % · testy: 79 C# + 97 Python
