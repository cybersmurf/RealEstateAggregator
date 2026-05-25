# AGENTS.md

This file provides guidance to AI coding agents (Copilot, Codex, Cursor, etc.) when working with this repository.

## Project Overview

**Real Estate Aggregator** – full-stack aplikace pro automatický sběr, normalizaci a AI analýzu realitních inzerátů z 14 českých zdrojů.

**Stack:** .NET 10 · Blazor Server (MudBlazor 9) · PostgreSQL 15 + PostGIS 3.4 + pgvector · Python FastAPI · Ollama · MCP (FastMCP 3.x)

**Aktuální stav (květen 2026):** ~1 558 aktivních inzerátů · 14 zdrojů · 97 % GPS pokrytí · AI: normalize 91 %, smart-tags 91 %, price-signal 79 %

---

## Quick Commands

```bash
# Docker (primární workflow)
make up              # Start full stack (6 services)
make down            # Stop containers
make rebuild-api     # Rebuild + restart API
make rebuild-app     # Rebuild + restart Blazor App
make rebuild-scraper # Rebuild + restart scraper
make logs-api        # Tail API logs
make status          # Health check all services
make test            # Run C# unit tests
make db              # psql console (realestate_dev)
make db-stats        # Listing counts by source
make scrape          # Incremental scrape all 14 sources
make scrape-full     # Full rescan (deactivates expired listings)

# .NET (local dev)
dotnet build
dotnet test tests/RealEstate.Tests
dotnet ef migrations add <Name> --project src/RealEstate.Infrastructure

# Python scraper (local dev)
cd scraper && source .venv/bin/activate
python run_api.py     # FastAPI :8001

# MCP server (local stdio)
cd mcp && python server.py
```

**Services:** App `:5002` · API `:5001` · Scraper `:8001` · MCP `:8002` · DB `:5432` · pgAdmin `:5050`

---

## Architecture

### Layer Map

```
src/RealEstate.Domain/         # Entities, Enums, interfaces (no deps)
src/RealEstate.Infrastructure/ # EF Core DbContext, Migrations, Repos
src/RealEstate.Api/            # Minimal API Endpoints + Services + DI (Program.cs)
src/RealEstate.App/            # Blazor Web App (MudBlazor 9)
src/RealEstate.Export/         # Export content builders (Markdown, Word)
src/RealEstate.Background/     # Background jobs (AnalysisJob)
tests/RealEstate.Tests/        # xUnit tests (79 C# tests)
scraper/                       # Python FastAPI scraping service (14 zdrojů)
mcp/server.py                  # FastMCP 3.x MCP server (14 tools)
```

### Request Flow

```
Blazor App :5002  →  .NET API :5001  →  PostgreSQL :5432
                                     →  Ollama :11434
                                     →  Python Scraper :8001  →  External sites
MCP server :8002  →  .NET API :5001
```

---

## Scrapers (14 zdrojů)

| CODE | Název |
|---|---|
| REMAX | RE/MAX Czech Republic |
| MMR | M&M Reality |
| PRODEJMETO | Prodejme.to |
| SREALITY | Sreality.cz |
| IDNES | iDnes Reality |
| CENTURY21 | CENTURY 21 |
| PREMIAREALITY | Premiera Reality |
| DELUXREALITY | Delux Reality |
| HVREALITY | HV Reality |
| LEXAMO | Lexamo |
| ZNOJMOREALITY | Znojmo Reality |
| NEMZNOJMO | Nemovitosti Znojmo |
| REAS | Reas.cz |
| BAZOS | Bazoš.cz |

Každý scraper je v `scraper/core/scrapers/<code>_scraper.py`. Runner: `scraper/core/runner.py`.

---

## API Endpoints (přehled)

### Listings
- `POST /api/listings/search` – seznam s filtry + paginací
- `GET /api/listings/{id}` – detail
- `GET /api/listings/{id}/price-history` – historie cen
- `POST /api/listings/{id}/state` – user stav (Liked/ToVisit/Visited/Disliked)
- `GET /api/listings/export.csv` – CSV export (UTF-8 BOM, semicolony)
- `POST /api/listings/deactivate-dead` – HTTP HEAD check + deaktivace
- `GET /api/listings/my-listings` – inzeráty se stavem (seskupené)

### Scraping (chráněno `X-Api-Key` hlavičkou)
- `POST /api/scraping/trigger`
- `POST /api/scraping-playwright/run` (REMAX Playwright)

### AI/Ollama
- `POST /api/ollama/bulk-normalize` – normalizace dat Ollama
- `POST /api/ollama/bulk-smart-tags` – generování smart tagů
- `POST /api/ollama/bulk-price-opinion` – odhad ceny
- `GET /api/ollama/stats` – statistiky AI zpracování

### RAG
- `POST /api/rag/ask` – AI chat napříč inzeráty
- `POST /api/rag/embed-descriptions` – batch embedding popisů
- `GET /api/rag/status` – stav RAG (provider, počty)
- `POST /api/listings/{id}/ask` – AI chat pro jeden inzerát
- `POST /api/listings/{id}/analyses` – uložit analýzu + embedding
- `GET /api/listings/{id}/analyses` – seznam analýz

### Spatial (PostGIS)
- `POST /api/spatial/bulk-geocode` – Nominatim geocoding
- `POST /api/spatial/corridor` – OSRM + ST_Buffer koridor
- `GET /api/spatial/map-points` – všechny body pro Leaflet mapu

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
- `GET /api/auth/drive/setup`, `/api/auth/onedrive/setup`

---

## MCP Tools (14 nástrojů)

MCP server (`mcp/server.py`) používá FastMCP 3.x a poskytuje přístup k API z Claude Desktop.

| Tool | Popis |
|---|---|
| `search_listings` | Vyhledat inzeráty (text + filtry) |
| `get_listing` | Kompletní detail + ZÁPIS Z PROHLÍDKY + Drive URL |
| `get_inspection_photos` | Fotky z prohlídky (user_listing_photos) |
| `get_listing_photos` | Fotky z inzerátu |
| `analyze_inspection_photos` | Vision analýza fotek z prohlídky |
| `analyze_listing_photos` | Vision analýza fotek inzerátu |
| `analyze_tovisit_listings` | Analýza všech inzerátů k návštěvě |
| `get_analyses` | Všechny uložené analýzy (plný obsah) |
| `save_analysis` | Uložit analýzu + auto-embedding |
| `ask_listing` | RAG chat pro jeden inzerát |
| `ask_general` | RAG chat přes všechny inzeráty |
| `list_sources` | Přehled aktivních zdrojů |
| `get_rag_status` | Stav RAG (embeddingy, provider) |
| `embed_description` | Embed popisu inzerátu |

---

## Code Conventions

### C# (.NET 10 / C# 12)

```csharp
// Primary constructors
public sealed class ListingService(RealEstateDbContext ctx, ILogger<ListingService> logger) { }

// Records pro DTOs (nikdy AutoMapper)
public record ListingSummaryDto(Guid Id, string Title, decimal? Price, string LocationText);

// Minimal APIs s MapGroup
app.MapGroup("/api/listings").MapPost("/search", SearchListings);

// HasConversion – NIKDY Enum.Parse (nefunguje v EF expression trees)
// Správně: switch expression
v == "House" ? PropertyType.House : v == "Apartment" ? PropertyType.Apartment : ...

// Null checks
if (value is null) ...  // nikdy == null
```

**Povinné vzory:**
- `AsNoTracking()` na všech read-only EF dotazech
- `CancellationToken` parameter na každé async metodě
- File-scoped namespaces
- `IDisposable` + `CancellationTokenSource` v Blazor komponentách dělajících HTTP volání

### Blazor (MudBlazor 9)

```razor
@* Vždy explicitní type parametry *@
<MudChip T="string" Size="Size.Small">@item.SourceName</MudChip>
<MudCarousel TData="object" Style="height:400px;">

@* User feedback přes ISnackbar *@
Snackbar.Add("Uloženo!", Severity.Success);
```

### Python

```python
# Vždy async/await pro I/O
async with httpx.AsyncClient() as client: ...

# Type hints povinné
def _parse_price(self, text: str) -> Optional[decimal.Decimal]: ...

# Defensive HTML parsing
h1 = soup.find('h1')
title = h1.get_text() if h1 else "Unknown"

# Photo upsert v transakci
async with conn.transaction():
    await conn.execute("DELETE FROM ... WHERE listing_id = $1", listing_id)
    for photo in photos[:20]: ...
```

---

## Database Schema

- Schéma: `re_realestate`
- Naming: **snake_case** (EFCore.NamingConventions)
- PK: `Guid`
- Enum values: anglicky (`House`, `Apartment`, `Sale`, `Rent`, `Auction`)

### Hlavní tabulky

| Tabulka | Popis |
|---|---|
| `listings` | Normalizované inzeráty (SmartTags, PriceSignal, embeddings, GPS) |
| `listing_photos` | Fotografie inzerátů (max 20) |
| `listing_analyses` | AI analýzy + pgvector embeddings |
| `listing_cadastre_data` | KN data z RUIAN + KN OCR |
| `listing_price_history` | Historie cen (backfill + průběžné sledování) |
| `user_listing_states` | Stav inzerátu per uživatel (Liked/ToVisit/...) |
| `user_listing_photos` | Fotky z prohlídky |
| `sources` | Zdroje inzerátů (realitní kanceláře) |
| `spatial_areas` | Uložené prostorové oblasti (koridory) |

### Enum HasConversion

```csharp
// SPRÁVNĚ (switch expression):
v == "House" ? PropertyType.House
: v == "Apartment" ? PropertyType.Apartment
: v == "Land" ? PropertyType.Land
: v == "Cottage" ? PropertyType.Cottage
: v == "Commercial" ? PropertyType.Commercial
: PropertyType.Other

// OfferType:
v == "Rent" ? OfferType.Rent
: v == "Auction" ? OfferType.Auction
: OfferType.Sale
```

---

## Key Patterns

### Upsert (Python scraper)

```python
# Check by (source_id, external_id)
existing = await conn.fetchrow("SELECT id FROM ... WHERE source_id=$1 AND external_id=$2", ...)
if existing:
    await conn.execute("UPDATE ...")
else:
    await conn.execute("INSERT ...")
# Po upsert: log cenové změny → INSERT INTO listing_price_history
```

### Enum Mapping (Czech → English)

```python
property_type_map = {
    "Dům": "House", "Byt": "Apartment", "Pozemek": "Land",
    "Chata": "Cottage", "Komerční": "Commercial", "Ostatní": "Other",
}
offer_type_map = {"Prodej": "Sale", "Pronájem": "Rent", "Dražba": "Auction"}
```

### DB Connection (Python)

```python
# Správně – acquire context manager
async with db_manager.acquire() as conn:
    result = await conn.fetch("SELECT ...")
# NIKDY db_manager.get_connection() – neexistuje!
```

### AI Šablony (runtime editovatelné)

```
src/RealEstate.Api/Templates/
  ai_instrukce_existing.md   ← existující nemovitosti
  ai_instrukce_newbuild.md   ← novostavby
```

Editovatelné bez recompilace. V Docker: `docker cp src/RealEstate.Api/Templates/<file> realestate-api:/app/Templates/`

### Monitoring / Slack

Slack alerting je v `scraper/core/notifications.py`. Webhook URL se nastavuje přes env `SLACK_WEBHOOK_URL`.
Health endpoint: `GET /v1/health/scrapers` na scraper API.

---

## Testing

### C# (xUnit) – 79 testů

```bash
dotnet test tests/RealEstate.Tests
dotnet test tests/RealEstate.Tests --filter "FullyQualifiedName~ExportBuilder"
```

Soubory: `CadastreTests.cs` (24), `ExportBuilderTests.cs` (23), `RagServiceTests.cs` (19), `UnitTest1.cs` (13)

### Python (pytest) – 97 testů

```bash
cd scraper && pytest
```

Soubory: `test_parsers.py` (62), `test_filters.py` (28), `test_enrichment.py` (7)

### Konvence
- C#: `[Fact]` a `[Theory]` + `[InlineData]`, naming `MethodName_Scenario_ExpectedBehavior`
- Bez "Arrange/Act/Assert" komentářů
- Python: `def test_<popis>(...)`

---

## Configuration

### Key Environment Variables (API)

| Proměnná | Popis |
|---|---|
| `API_KEY` | Chrání `/api/scraping/*` (`X-Api-Key` header) |
| `DB_HOST/PORT/NAME/USER/PASSWORD` | PostgreSQL connection |
| `SCRAPER_API_BASE_URL` | Python scraper URL (`http://scraper:8001` v Dockeru) |
| `Ollama__BaseUrl` | Ollama (`http://host.docker.internal:11434`) |
| `Ollama__VisionModel` | Vision model (`llama3.2-vision:11b`) |
| `PHOTOS_PUBLIC_BASE_URL` | Base URL pro stored fotky |

### Key Environment Variables (Scraper)

| Proměnná | Popis |
|---|---|
| `SLACK_WEBHOOK_URL` | Slack notifikace o chybách scrapingu |
| `DB_*` | PostgreSQL connection (host=`postgres` v Dockeru) |

---

## Common Pitfalls

| Problém | Řešení |
|---|---|
| Filtry vrací 0 výsledků | DB ukládá anglicky – nikdy české hodnoty v HasConversion |
| C# změna v Dockeru bez efektu | `docker compose build --no-cache api app && docker compose up -d --no-deps api app` |
| `db_manager.get_connection()` AttributeError | Použij `async with db_manager.acquire() as conn:` |
| Kontejnery po restartu Macu neběží | Zkontroluj `restart: unless-stopped` ve všech 6 službách |
| Lokální dotnet process stíní Docker port :5001 | `lsof -i :5001 -P -n` → `kill <PID>` |
| Nový .cs soubor není v Dockeru | `git add src/ && git commit && git push` PŘED deployem |
| EF Core CS8198 – `out` v expression tree | Použij switch expression místo `Enum.TryParse` v HasConversion |
| AI šablona se nezměnila po `docker cp` | Změna přežije jen do restartu – pro trvalou změnu rebuild api image |
| SReality dražba URL → 404 | Expected behavior (dražba skončila), deaktivuje se při `full_rescan` |

---

## Deployment (Production: 192.168.11.2 / realestate.sudata.eu)

```bash
# Viz docs/DEPLOYMENT.md nebo .github/skills/deploy-server/SKILL.md
git add src/ && git commit -m "feat: ..." && git push
make rebuild-api   # na serveru
```

**VŽDY git add + commit + push PŘED deployem** – jinak Docker build nevidí nové `.cs` soubory (ghost debugging).

---

*Aktualizováno: 25. května 2026 | Sessions 1–28+ | 14 scraperů | 79 C# testů + 97 Python testů*
