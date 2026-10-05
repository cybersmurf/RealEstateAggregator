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
scraper/                      # Python FastAPI scraping service (21 sources)
mcp/server.py                 # FastMCP 3.x MCP server (15 tools)
```

### API endpoint organization

Endpoints are registered in `src/RealEstate.Api/Endpoints/` as extension methods on `WebApplication`, then wired in `Program.cs`. Services are in `src/RealEstate.Api/Services/` behind interfaces registered in `ServiceCollectionExtensions.cs`.

**Fotky od makléře (od 5. 10. 2026):** Drive export zakládá tři podsložky – `Fotky_z_inzeratu`, `Moje_fotky_z_prohlidky`, `Fotky_od_maklere`. Do třetí ukládá Claude Desktop fotky, které pošle makléř (Úschovna, příloha mailu), roztříděné do podsložek `NN_Kategorie` + `FOTKY_OD_MAKLERE.md` (tabulka Složka | Počet | Co je na fotkách, řádek `**Zdroj:**`). `GET /api/listings/{id}/broker-photos` je čte přímo z Drivu (podsložka se hledá podle názvu, soubory se zobrazují přes `drive.google.com/thumbnail` – dědí sdílení ze složky; nic se nekopíruje), detail inzerátu má sekci „Fotky od makléře“, MCP `get_listing_photos(set="makler")`. Parser poznámek: `BrokerPhotoNotes`.

**Zmizelé z trhu:** `POST /api/listings/search` s `deactivatedSince` vrací neaktivní inzeráty stažené od data, u kterých už neběží ani kopie ve skupině duplicit (řazení `deactivated`); MCP `search_listings(gone_in_days=N)`. Bez toho filtru hledání vrací jen aktivní.

Long-running listing actions (photo classification `bulk-classify`, alt texts, `analyze-local`, `export-drive`, `export-analysis-to-drive`) run as server-side background jobs (`IBackgroundJobService`, in-memory, cancelled only on app shutdown). `?wait=false` returns 202 + `jobId`, default waits as before; status via `GET /api/jobs/{id}` and `GET /api/jobs?listingId=&active=true`. The Blazor detail page starts jobs with `wait=false`, polls them and resumes tracking after navigation, so leaving the page never aborts the work.

### Accounts, plans, authorization

`CurrentUserMiddleware` fills scoped `ICurrentUser`: `Authorization: Bearer` (HMAC token from `/api/auth/login`) → user; master `X-Api-Key` (= `API_KEY`) → default admin (used by Blazor App for `/api/scraping`, MCP, scraper); customer key `rea_…` → its owner with a daily quota; nothing → anonymous (no personal states, no original description). Gate endpoints with `.RequireAuth()`, `.RequireAdmin()`, `.RequirePlan(UserPlans.Hledac|Profi)` from `Helpers/AuthorizationFilters.cs` (402 when the plan is missing). Plans: `free` / `hledac` / `profi` (`UserPlans`). Blazor App logs in via form POST `/account/login` (cookie with the API token as claim) or via Stalwart OIDC (`/account/login-blackies` → `/api/auth/oidc`, owners only, see `docs/BLACKIES_SSO.md`); `ApiAuthHandler` adds the Bearer to API calls. Owner-only UI is wrapped in `<AuthorizeView Policy="Admin">`.

Legal constraints: the original listing text is returned only to admins – public UI shows `summary` (Ollama, filled by `AiSummaryHostedService`); listing photos are displayed from the source URL, stored copies are deleted after classification and `/uploads/listings/*/photos` returns 404.

### Database schema

All tables live in the `re_realestate` schema. Column naming is **snake_case** (enforced by `UseSnakeCaseNamingConvention()`). Primary keys are `Guid`. Enum values stored as English strings (House/Apartment/Sale/Rent/Auction). pgvector extension is required; the `listings.description_embedding` column is 768-dim (nomic-embed-text).

EF Core configuration is done manually in `RealEstateDbContext.OnModelCreating` – there is no fluent API auto-discovery. Always add explicit `HasColumnName` calls.

### Python scraper

Each scraper is a class in `scraper/core/scrapers/` (new source = class + import/task in `runner.py` + seed row in `DbInitializer.cs` + logo in `SourceLogoProvider.cs`). Realingo (`REALINGO`, Next.js `__NEXT_DATA__`) skips offers whose `externalUrl` points to a source we scrape ourselves; REALmix (`REALMIX`, reality-znojmo.cz = sousede.cz = jihomoravskereality.cz) covers the small agencies without Sreality. Added 30. 9. 2026 from the South Moravia portal survey: Reality Čechy (`REALITYCECHY`), RealityMIX (`REALITYMIX`), Realcity (`REALCITY`), Bezrealitky (`BEZREALITKY`, public GraphQL API, private sellers) and the auction portal OK dražby (`OKDRAZBY`, `offer_type` Dražba). Edrazby.cz and the Český internet regional network were skipped because their robots.txt forbids paging or the whole site. The runner (`scraper/core/runner.py`) orchestrates all scrapers, calling `full_rescan` (deactivates unseen listings) or incremental mode. Scrapers write directly to PostgreSQL via `asyncpg` using upsert patterns. Max 20 photos per listing are stored.

**Broker contact** (`listings.seller_name/_email/_phone/_company`, since 1. 10. 2026): filled by the Sreality scraper from the v1 detail (`user` + `premise`), by RealityMIX from the detail sidebar and by Reality Čechy without the phone (the portal reveals it only after a click); other sources leave it NULL and the upsert keeps what is known (`COALESCE`). `GET /api/listings/{id}` returns `seller*` **only to admins** (third-party personal data) and borrows the contact from a duplicate-group member when the opened listing has none (`sellerFromSourceCode`). MCP `get_listing` prints it as **Makléř**.

**Sreality detail URLs**: `/detail/{type}/{main}/{sub}/{locality}/{hash_id}`. Sreality 301-redirects to the canonical address whenever every segment is *some* valid slug, and returns 404 when a segment is missing or unknown. `_CAT_SUB_SLUG` holds the 48 sub-type slugs verified against live listings (auctions are `drazby`); an unknown code falls back to `rodinny` / locality `x` instead of dropping the segment. New sub-type code = look up the redirect of `/detail/prodej/dum/rodinny/x/<hash_id>` and add it.

### AI/RAG pipeline

1. `OllamaEmbeddingService` (or `OpenAIEmbeddingService`) generates 768-dim vectors.
2. Vectors stored in `listings.description_embedding` and `listing_analyses.embedding`.
3. `RagService` performs cosine similarity search via pgvector IVFFlat index.
4. `PhotoClassificationService` classifies listing photos into 13 categories with a cloud vision model: `google/gemini-3.1-flash-lite` via OpenRouter, falling back to Mistral (`mistral-medium-latest`). `PhotoDamageValidator` keeps `damage_detected` only when the model backed it with a damage label, `damage_evidence`, or the description, and never on a render (label `visualization`, set from the prompt's `is_visualization`; such photos get description "Vizualizace: …" and the analysis prompt marks them as not the real condition). Model choice comes from a 9-model benchmark (Sept 2026); local Ollama vision models were too slow (~20 s/photo).
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
| `OpenRouter__ApiKey` / `Groq__ApiKey` / `Mistral__ApiKey` | Cloud LLM. Text chat (shrnutí, štítky, normalizace, cenový signál) jde řetězcem Mistral → OpenRouter → Groq → Ollama Cloud (`*__ChatModel` přepíše model); poskytovatel s 401/402/403 je na 30 min vyřazen (`LlmProviderCooldown`) |
| `Anthropic__ApiKey` / `OllamaCloud__ApiKey` | Cloud LLM fallback |
| `SLACK_WEBHOOK_URL` | Slack notifikace chyb ze scraperu |
| `SKIP_EF_MIGRATIONS` | `true` = přeskočí bootstrap schématu při startu API |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | Výchozí admin účet (Id `…0001`, na něm jsou Petrovy stavy inzerátů); e-mail i heslo se při startu API srovnají s proměnnými. Na produkci `petr@blackies.cz` = stejný účet jako přihlášení přes Blackies |
| `AUTH_SECRET` | Podpis bearer tokenů (prázdné = odvozeno z `API_KEY`) |
| `OIDC_CLIENT_ID` / `OIDC_CLIENT_SECRET` / `OIDC_ADMIN_EMAILS` | Přihlášení vlastníků účtem Blackies pošta (Stalwart OIDC); adresy v seznamu dostanou admin. Bez `OIDC_CLIENT_ID` se tlačítko neukáže. Návrh v `docs/BLACKIES_SSO.md` |
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
Lokální `make up` je jen pro vývoj. Deploy: `make deploy-api` / `make deploy-app` / `make deploy-scraper` / `make deploy-mcp`.

## Database Migrations

New migrations require running from `src/RealEstate.Api` (startup project). SQL-only migrations go in `scripts/` as `migrate_*.sql` files and are applied manually via `make db`.

The app calls `EnsureCreatedAsync` in Development (not `MigrateAsync`) to avoid column naming conflicts with existing production schema.
