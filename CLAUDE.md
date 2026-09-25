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
scraper/                      # Python FastAPI scraping service (14 sources)
mcp/server.py                 # FastMCP 3.x MCP server (15 tools)
```

### API endpoint organization

Endpoints are registered in `src/RealEstate.Api/Endpoints/` as extension methods on `WebApplication`, then wired in `Program.cs`. Services are in `src/RealEstate.Api/Services/` behind interfaces registered in `ServiceCollectionExtensions.cs`.

### Accounts, plans, authorization

`CurrentUserMiddleware` fills scoped `ICurrentUser`: `Authorization: Bearer` (HMAC token from `/api/auth/login`) → user; master `X-Api-Key` (= `API_KEY`) → default admin (used by Blazor App for `/api/scraping`, MCP, scraper); customer key `rea_…` → its owner with a daily quota; nothing → anonymous (no personal states, no original description). Gate endpoints with `.RequireAuth()`, `.RequireAdmin()`, `.RequirePlan(UserPlans.Hledac|Profi)` from `Helpers/AuthorizationFilters.cs` (402 when the plan is missing). Plans: `free` / `hledac` / `profi` (`UserPlans`). Blazor App logs in via form POST `/account/login` (cookie with the API token as claim); `ApiAuthHandler` adds the Bearer to API calls. Owner-only UI is wrapped in `<AuthorizeView Policy="Admin">`.

Legal constraints: the original listing text is returned only to admins – public UI shows `summary` (Ollama, filled by `AiSummaryHostedService`); listing photos are displayed from the source URL, stored copies are deleted after classification and `/uploads/listings/*/photos` returns 404.

### Database schema

All tables live in the `re_realestate` schema. Column naming is **snake_case** (enforced by `UseSnakeCaseNamingConvention()`). Primary keys are `Guid`. Enum values stored as English strings (House/Apartment/Sale/Rent/Auction). pgvector extension is required; the `listings.description_embedding` column is 768-dim (nomic-embed-text).

EF Core configuration is done manually in `RealEstateDbContext.OnModelCreating` – there is no fluent API auto-discovery. Always add explicit `HasColumnName` calls.

### Python scraper

Each scraper is a class in `scraper/core/scrapers/`. The runner (`scraper/core/runner.py`) orchestrates all scrapers, calling `full_rescan` (deactivates unseen listings) or incremental mode. Scrapers write directly to PostgreSQL via `asyncpg` using upsert patterns. Max 20 photos per listing are stored.

### AI/RAG pipeline

1. `OllamaEmbeddingService` (or `OpenAIEmbeddingService`) generates 768-dim vectors.
2. Vectors stored in `listings.description_embedding` and `listing_analyses.embedding`.
3. `RagService` performs cosine similarity search via pgvector IVFFlat index.
4. `PhotoClassificationService` classifies listing photos into 13 categories with a cloud vision model: `google/gemini-3.1-flash-lite` via OpenRouter, falling back to Mistral (`mistral-medium-latest`). `PhotoDamageValidator` keeps `damage_detected` only when the model backed it with a damage label, `damage_evidence`, or the description. Model choice comes from a 9-model benchmark (Sept 2026); local Ollama vision models were too slow (~20 s/photo).
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
| `OPENROUTER_VISION_MODEL` | Primary photo classification model (default: `google/gemini-3.1-flash-lite`) |
| `MISTRAL_VISION_MODEL` | Fallback photo classification + cadastre OCR model (default: `mistral-medium-latest`) |
| `PHOTO_VISION_PROVIDER` | `mistral` / `openrouter` = force a single provider for photo classification |
| `PHOTOS_PUBLIC_BASE_URL` | Base URL for serving stored photos |
| `PUBLIC_API_URL` | Externí URL API – plní `PHOTOS_PUBLIC_BASE_URL` a `ApiPublicUrl` |
| `OpenRouter__ApiKey` / `Groq__ApiKey` / `Mistral__ApiKey` | Cloud LLM fallback |
| `Anthropic__ApiKey` / `OllamaCloud__ApiKey` | Cloud LLM fallback |
| `SLACK_WEBHOOK_URL` | Slack notifikace chyb ze scraperu |
| `SKIP_EF_MIGRATIONS` | `true` = přeskočí bootstrap schématu při startu API |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | Výchozí admin účet (Id `…0001`); heslo se při startu API srovná s proměnnou |
| `AUTH_SECRET` | Podpis bearer tokenů (prázdné = odvozeno z `API_KEY`) |
| `APP_PUBLIC_URL` | Odkazy v e-mailech, návrat ze Stripe (`https://realestate.sudata.eu`) |
| `STRIPE_SECRET_KEY` / `STRIPE_WEBHOOK_SECRET` / `STRIPE_PRICE_*` | Předplatné tarifů Hledač/Profi; bez klíče checkout vrací 503 |
| `SMTP_HOST/PORT/USER/PASSWORD/FROM` | E-mailová upozornění uložených hledání, přeposílání leadů (`LEADS_NOTIFY_EMAIL`) |
| `TELEGRAM_BOT_TOKEN` | Telegram upozornění (uživatel zadá chat_id v účtu) |
| `PHOTOS_DELETE_STORED_AFTER_CLASSIFICATION` | `true` = soubor fotky inzerátu se po klasifikaci smaže |
| `MORTGAGE_PARTNER_URL` / `MORTGAGE_DEFAULT_RATE` | App: hypoteční partner (UTM), výchozí sazba kalkulačky |

Secrets (Google Drive) live in `secrets/` and `src/RealEstate.Api/secrets/` – never commit these.

## Kde běží produkce

Aplikace neběží lokálně – produkce je na domácím serveru **sudgate** (`192.168.11.2`, `realestate.sudata.eu`)
za Traefikem. Infrastruktura je verzovaná v samostatném repu `/Volumes/edata/dev/zupagate`
(Traefik routy, TLS přes Wedos DNS-01, DDNS relay, Prometheus/Grafana/Alertmanager, UFW + DOCKER-USER).
Lokální `make up` je jen pro vývoj. Deploy: `make deploy-api` / `make deploy-app`.

## Database Migrations

New migrations require running from `src/RealEstate.Api` (startup project). SQL-only migrations go in `scripts/` as `migrate_*.sql` files and are applied manually via `make db`.

The app calls `EnsureCreatedAsync` in Development (not `MigrateAsync`) to avoid column naming conflicts with existing production schema.
