# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Docker (primary workflow – everything runs in Docker)

```bash
make up              # Start full stack (postgres, api, app, scraper, mcp)
make down            # Stop containers (data preserved)
make rebuild-api     # Build + restart just the API
make rebuild-app     # Build + restart just Blazor App
make rebuild-scraper # Build + restart just Python scraper
make logs-api        # Tail API logs
make status          # Health check all services
make test            # Run unit tests
make db              # psql console (realestate_dev)
make db-stats        # Listing counts by source
make scrape          # Incremental scrape all sources
make scrape-full     # Full rescan all sources
```

Services: App `:5002`, API `:5001`, Scraper `:8001`, MCP `:8002`, DB `:5432`

### .NET (local dev, outside Docker)

```bash
dotnet build                        # Build entire solution
dotnet test tests/RealEstate.Tests  # Run all tests
dotnet test tests/RealEstate.Tests --filter "FullyQualifiedName~ExportBuilder"  # Single test class

# EF Core migrations (run from src/RealEstate.Api)
dotnet ef migrations add <Name> --project ../RealEstate.Infrastructure
dotnet ef database update --project ../RealEstate.Infrastructure
dotnet ef migrations script --idempotent  # Generate SQL
```

### Python scraper (local dev)

```bash
cd scraper
source .venv/bin/activate
python run_api.py                   # Start FastAPI server on :8001
pytest                              # Run tests
```

### MCP server (local stdio)

```bash
cd mcp
API_BASE_URL=http://localhost:5001 python server.py
```

## Architecture

### Layer map

```
src/RealEstate.Domain/        # Entities, Enums, Repository interfaces (no dependencies)
src/RealEstate.Infrastructure/ # EF Core DbContext, Migrations, Repositories, Background services
src/RealEstate.Api/           # Minimal API endpoints + Services + DI wiring (Program.cs)
src/RealEstate.App/           # Blazor Web App (MudBlazor 9)
src/RealEstate.Export/        # Export content builders (Markdown, Word)
src/RealEstate.Background/    # Background job services
tests/RealEstate.Tests/       # xUnit tests
scraper/                      # Python FastAPI scraping service (12 sources)
mcp/server.py                 # FastMCP 3.x MCP server (14 tools)
```

### API endpoint organization

Endpoints are registered in `src/RealEstate.Api/Endpoints/` as extension methods on `WebApplication`, then wired in `Program.cs`. Scraping endpoints require `X-Api-Key` header. All other endpoints are public. Services are in `src/RealEstate.Api/Services/` behind interfaces registered in `ServiceCollectionExtensions.cs`.

### Database schema

All tables live in the `re_realestate` schema. Column naming is **snake_case** (enforced by `UseSnakeCaseNamingConvention()`). Primary keys are `Guid`. Enum values stored as English strings (House/Apartment/Sale/Rent/Auction). pgvector extension is required; the `listings.description_embedding` column is 768-dim (nomic-embed-text).

EF Core configuration is done manually in `RealEstateDbContext.OnModelCreating` – there is no fluent API auto-discovery. Always add explicit `HasColumnName` calls.

### Python scraper

Each scraper is a class in `scraper/core/scrapers/`. The runner (`scraper/core/runner.py`) orchestrates all scrapers, calling `full_rescan` (deactivates unseen listings) or incremental mode. Scrapers write directly to PostgreSQL via `asyncpg` using upsert patterns. Max 20 photos per listing are stored.

### AI/RAG pipeline

1. `OllamaEmbeddingService` (or `OpenAIEmbeddingService`) generates 768-dim vectors.
2. Vectors stored in `listings.description_embedding` and `listing_analyses.embedding`.
3. `RagService` performs cosine similarity search via pgvector IVFFlat index.
4. `PhotoClassificationService` uses `llama3.2-vision:11b` to classify listing photos into 13 categories.
5. Embedding provider is selected at startup: `Embedding:Provider=ollama` → Ollama, otherwise OpenAI.

## Code Conventions

### C# (.NET 10 / C# 12)

- **Primary constructors** for all services: `public sealed class MyService(RealEstateDbContext ctx, ILogger<MyService> logger)`
- **Records** for all DTOs; never AutoMapper – always manual mapping
- **Minimal APIs** with `MapGroup` for endpoint organization
- `AsNoTracking()` on all read-only EF Core queries
- `CancellationToken` parameter on every async method
- Enums in `HasConversion`: use switch expressions, **never** `Enum.Parse()` (breaks EF expression trees)
- Null checks: use `is null` / `is not null`, never `== null`
- File-scoped namespaces throughout

### Blazor (MudBlazor 9)

- Always specify explicit type parameters: `<MudChip T="string">`, `<MudCarousel TData="object">`
- User feedback via `ISnackbar` (success/error)
- Filter state persisted with `ProtectedSessionStorage`
- Implement `IDisposable` + `CancellationTokenSource` for components making HTTP calls

### Python

- All DB and HTTP operations must be `async`/`await`
- Always use type hints on all functions
- Defensive HTML parsing: `h1 = soup.find('h1'); title = h1.get_text() if h1 else "Unknown"`
- Photo upserts must run inside a transaction (delete + insert pattern)

### Testing (xUnit)

- Tests in `tests/RealEstate.Tests/`; use `[Fact]` and `[Theory]` + `[InlineData]`
- No "Arrange/Act/Assert" comments
- Follow naming style of existing test files

## Key Configuration

Environment variables used by API (set in `docker-compose.yml` or `.env`):

| Variable | Purpose |
|---|---|
| `API_KEY` | Secures `/api/scraping/*` endpoints (header: `X-Api-Key`) |
| `DB_HOST/PORT/NAME/USER/PASSWORD` | PostgreSQL connection |
| `SCRAPER_API_BASE_URL` | Python scraper URL (default: `http://localhost:8001`) |
| `Ollama__BaseUrl` | Ollama endpoint (Docker: `http://host.docker.internal:11434`) |
| `Ollama__VisionModel` | Vision model for photo classification (default: `llama3.2-vision:11b`) |
| `PHOTOS_PUBLIC_BASE_URL` | Base URL for serving stored photos |

Secrets (Google Drive, OneDrive) live in `secrets/` and `src/RealEstate.Api/secrets/` – never commit these.

## Database Migrations

New migrations require running from `src/RealEstate.Api` (startup project). SQL-only migrations go in `scripts/` as `migrate_*.sql` files and are applied manually via `make db`.

The app calls `EnsureCreatedAsync` in Development (not `MigrateAsync`) to avoid column naming conflicts with existing production schema.
