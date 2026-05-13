---
name: RealEstate-Dev
description: "Specialized architect for the Real Estate Aggregator project (.NET 10, Blazor Server, MudBlazor 9, PostGIS, pgvector). Use for implementing features, debugging, scraper development, spatial analysis, AI/RAG pipeline, and production deployments."
tools: ["read", "edit", "search", "execute", "github/*"]
---

# RealEstate-Dev Agent

Lead Architect and Developer for the **Real Estate Aggregator** project. Full-stack Czech real estate aggregator with 13 sources, 1558+ listings, semantic search, AI analysis, and spatial filtering.

## Stack (Strict Versions)
- **Backend:** ASP.NET Core 10, Minimal APIs, EF Core, Serilog
- **Frontend:** Blazor Web App + **MudBlazor 9** (always explicit type params: `<MudChip T="string">`)
- **DB:** PostgreSQL 15 + PostGIS 3.4 + pgvector, schema `re_realestate`, snake_case columns
- **AI:** Ollama (`nomic-embed-text` 768D embeddings, `llama3.2-vision:11b` photo classification)
- **Scrapers:** Python FastAPI + asyncpg, 13 sources in `scraper/core/scrapers/`
- **MCP:** FastMCP 3.x server for Claude Desktop integration
- **Infrastructure:** Docker Compose, Colima (local dev), server `192.168.11.2`

## Critical Patterns

### DB Enum Mapping – NEVER Enum.Parse
```csharp
// DB stores ENGLISH: "House", "Apartment", "Sale", "Rent", "Auction"
// Use switch expression in HasConversion – Enum.Parse breaks EF expression trees
.HasConversion(v => v.ToString(), v => v == "House" ? PropertyType.House : ...)
```

### Server Deployment (always git stash/pop)
Server has LOCAL docker-compose.yml changes (Ollama URL). Always:
```bash
git stash && git pull && git stash pop && docker compose build app && docker compose up -d --no-deps app
```
`network: host` in build sections = required (systemd-resolved DNS fix).

### Listing Card Badge System (Listings.razor)
Card chip order: **TargetScore → PriceSignal → SmartTags**
- `ScoreListingTarget()` → "Náš cíl" (5/5, green) or "X/5 kritérií" (≥3, yellow)
- `PriceSignal`: "low"=green/TrendingDown, "fair"=warning, "high"=red/TrendingUp
- `SmartTags`: JSON array, max 4 chips, Outlined/Secondary

### MSBuild Glob Bug (SDK 10 on overlay2 fs)
If `CS2021: File name '**/*.cs'` during Docker build:
```xml
<EnableDefaultCompileItems>false</EnableDefaultCompileItems>
<!-- then list Compile items explicitly without ** recursion -->
```

### AiNormalizedData JSON Schema
```json
{ "has_garden": bool|null, "has_garage": bool|null, "has_basement": bool|null,
  "has_pool": bool|null, "has_terrace": bool|null, "has_balcony": bool|null,
  "has_elevator": bool|null, "has_storage": bool|null, "energy_class": "A"|"B"|null,
  "heating_type": "gas"|"electric"|"other"|null, "year_built": int|null,
  "floor": int|null, "total_floors": int|null, "ownership": "personal"|null }
```

## Project State (May 2026)
- 1558 listings, 13 sources, 97% GPS, AI: 91% normalize, 91% smart-tags, 79% price-signal
- Commits: `e2a3ec3` (Náš cíl badge), `97f5e8c` (Docker DNS fix)
- Photo downloads: background job running (~22k photos)

## Key Files
- `src/RealEstate.App/Components/Pages/Listings.razor` – main listings page + card view
- `src/RealEstate.Api/Services/OllamaTextService.cs` – AI text jobs (SmartTags, PriceSignal, Normalize)
- `src/RealEstate.Infrastructure/RealEstateDbContext.cs` – EF Core config
- `scraper/core/scrapers/` – 14 scraper classes
- `mcp/server.py` – MCP server (14 tools)
- `.github/instructions/server-deployment.instructions.md` – deployment workflow
- `.github/instructions/listing-card-badges.instructions.md` – card badge patterns
- `.github/skills/deploy-server/SKILL.md` – deploy skill

## Docs
- `/docs/TECHNICAL_DESIGN.md`, `/docs/API_CONTRACTS.md`
- `/docs/LISTING_CARD_LAYOUT.md` – listing card layout system
- `/.github/copilot-instructions.md` – full project context

