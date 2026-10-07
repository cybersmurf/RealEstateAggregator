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
scraper/                      # Python FastAPI scraping service (22 sources)
mcp/server.py                 # FastMCP 3.x MCP server (20 tools)
```

### API endpoint organization

Endpoints are registered in `src/RealEstate.Api/Endpoints/` as extension methods on `WebApplication`, then wired in `Program.cs`. Services are in `src/RealEstate.Api/Services/` behind interfaces registered in `ServiceCollectionExtensions.cs`.

**Fotky od makléře (od 5. 10. 2026):** Drive export zakládá tři podsložky – `Fotky_z_inzeratu`, `Moje_fotky_z_prohlidky`, `Fotky_od_maklere`. Do třetí ukládá Claude Desktop fotky, které pošle makléř (Úschovna, příloha mailu), roztříděné do podsložek `NN_Kategorie` + `FOTKY_OD_MAKLERE.md` (tabulka Složka | Počet | Co je na fotkách, řádek `**Zdroj:**`). `GET /api/listings/{id}/broker-photos` je čte přímo z Drivu (podsložka se hledá podle názvu, soubory se zobrazují přes `drive.google.com/thumbnail` – dědí sdílení ze složky; nic se nekopíruje), detail inzerátu má sekci „Fotky od makléře“, MCP `get_listing_photos(set="makler")`. Parser poznámek: `BrokerPhotoNotes`.

**Změny cen v hledání:** `priceChangedSince` (+ `priceDropsOnly`) vrací inzeráty se změnou ceny od data podle `listing_price_history`; výsledky nesou `previousPrice`, `priceChangePct`, `priceChangedAt`. MCP `search_listings(price_changed_in_days=N, price_drops_only=True)` a změnu vypisuje u ceny.

**Záznam uživatele patří domu, ne kopii inzerátu (od 6. 10. 2026):** `DuplicateDetectionService.AttachRemembered` připojí stažené inzeráty s uživatelovým záznamem (stav ≠ New, poznámky, `user_listing_photos`) k živé kopii téhož domu – aktivní párování nemění, jen při jednoznačné shodě. Inzerát bez vlastního stavu ho převezme od kopie ve skupině (`UserStateFromListingId` v detailu i seznamu, filtr `userStatus`, `GET /api/listings/{id}/inspection-photos` vrací fotky celé skupiny). „Moje inzeráty" a filtr `Visited` ukazují i stažené; stažený záznam s živou kopií se zobrazí jako karta té živé. `upload-inspection-photos` fotky jen přidává.

**Poznámka k ceně:** scraper smí poslat `price_note` (upsert ji vždy přepíše) a `keep_last_price` – rezervovaná nabídka bez ceny si nechá poslední známou. Premia Reality: `td.prodano` → deaktivace, `td.rezervace` → štítek „Rezervace".

**Zmizelé z trhu:** `POST /api/listings/search` s `deactivatedSince` vrací neaktivní inzeráty stažené od data, u kterých už neběží ani kopie ve skupině duplicit (řazení `deactivated`); MCP `search_listings(gone_in_days=N)`. Bez toho filtru hledání vrací jen aktivní.

Long-running listing actions (photo classification `bulk-classify`, alt texts, `analyze-local`, `export-drive`, `export-analysis-to-drive`) run as server-side background jobs (`IBackgroundJobService`, in-memory, cancelled only on app shutdown). `?wait=false` returns 202 + `jobId`, default waits as before; status via `GET /api/jobs/{id}` and `GET /api/jobs?listingId=&active=true`. The Blazor detail page starts jobs with `wait=false`, polls them and resumes tracking after navigation, so leaving the page never aborts the work.

### Accounts, plans, authorization

`CurrentUserMiddleware` fills scoped `ICurrentUser`: `Authorization: Bearer` (HMAC token from `/api/auth/login`) → user; master `X-Api-Key` (= `API_KEY`) → default admin (used by Blazor App for `/api/scraping`, MCP, scraper); customer key `rea_…` → its owner with a daily quota; nothing → anonymous (no personal states, no original description). Gate endpoints with `.RequireAuth()`, `.RequireAdmin()`, `.RequirePlan(UserPlans.Hledac|Profi)` from `Helpers/AuthorizationFilters.cs` (402 when the plan is missing). Plans: `free` / `hledac` / `profi` (`UserPlans`). Blazor App logs in via form POST `/account/login` (cookie with the API token as claim) or via Stalwart OIDC (`/account/login-blackies` → `/api/auth/oidc`, owners only, see `docs/BLACKIES_SSO.md`); `ApiAuthHandler` adds the Bearer to API calls. Owner-only UI is wrapped in `<AuthorizeView Policy="Admin">`.

**Shared workspace (since 6. 10. 2026):** `workspace_members` (owner, e-mail, role `reader` / `writer`; bound to the e-mail so a member can be invited before registering). `CurrentUserMiddleware` resolves it per request: a member's `ICurrentUser.EffectiveUserId` is the owner's id, so listing states and notes are literally shared; `CanWriteWorkspace` is false for readers (`POST /api/listings/{id}/state` → 403). Inspection photos, analyses and photo comparisons are not per-user data, so `.RequireInspectionRecords()` lets in admins and members of an **admin's** workspace only – this now guards `GET …/analyses` (it used to be public), `…/inspection-photos`, `…/inspection-comparison` and the RAG `ask` endpoints. Members are managed by admins on the Account page (`/api/workspace/members`). The App carries the role in cookie claims (`workspace_role`, `inspection_records`, policy `InspectionRecords`), so a change takes effect at the member's next login. Analyses and inspection photos are returned for the whole duplicate group; inspection thumbnails (480 px) are generated on first read into `inspection/thumbs/`.

Legal constraints: the original listing text is returned only to admins – public UI shows `summary` (Ollama, filled by `AiSummaryHostedService`); listing photos are displayed from the source URL, stored copies are deleted after classification and `/uploads/listings/*/photos` returns 404.

### Database schema

All tables live in the `re_realestate` schema. Column naming is **snake_case** (enforced by `UseSnakeCaseNamingConvention()`). Primary keys are `Guid`. Enum values stored as English strings (House/Apartment/Sale/Rent/Auction). pgvector extension is required; the `listings.description_embedding` column is 768-dim (nomic-embed-text).

EF Core configuration is done manually in `RealEstateDbContext.OnModelCreating` – there is no fluent API auto-discovery. Always add explicit `HasColumnName` calls.

### Python scraper

Each scraper is a class in `scraper/core/scrapers/` (new source = class + import/task in `runner.py` + seed row in `DbInitializer.cs` + logo in `SourceLogoProvider.cs`). Realingo (`REALINGO`, Next.js `__NEXT_DATA__`) skips offers whose `externalUrl` points to a source we scrape ourselves; REALmix (`REALMIX`, reality-znojmo.cz = sousede.cz = jihomoravskereality.cz) covers the small agencies without Sreality. Added 30. 9. 2026 from the South Moravia portal survey: Reality Čechy (`REALITYCECHY`), RealityMIX (`REALITYMIX`), Realcity (`REALCITY`), Bezrealitky (`BEZREALITKY`, public GraphQL API, private sellers) and the auction portal OK dražby (`OKDRAZBY`, `offer_type` Dražba). Edrazby.cz and the Český internet regional network were skipped because their robots.txt forbids paging or the whole site. Added 6. 10. 2026: UlovDomov (`ULOVDOMOV`) – its API host forbids crawling in robots.txt, so the scraper reads `sitemap-offers.xml` (static, refreshed only on the portal's deploys) and the SSR detail `__NEXT_DATA__`; candidates are pre-selected by municipality slug, the district always comes from the detail. Mostly agency re-posts (first full run: 642 active, 389 paired as duplicates), the added value is rentals; its GPS is precise (median 21 m from the Sreality copy). Sbazar was skipped (`User-agent: *` → `Disallow: /`). The runner (`scraper/core/runner.py`) orchestrates all scrapers, calling `full_rescan` (deactivates unseen listings) or incremental mode. Scrapers write directly to PostgreSQL via `asyncpg` using upsert patterns. Max 20 photos per listing are stored.

**Coverage audit 6. 10. 2026** (all sources compared with what the portals list for okres Znojmo and Brno-venkov). Lessons that now hold for every scraper:
- **District before the geo filter.** `filters.py` matches target districts as substrings of location text + `district`, so "ulice Dlouhá, Hrabětice" used to be dropped. A scraper must set `district` when the source states it (search config, URL slug, list item); otherwise `database._derive_district` fills it before the filter from GPS (district polygons) and, for local sources listed in `_DISTRICT_BY_NAME_SOURCES`, from the municipality name.
- **Crawl the portal's per-district result list**, never match place names in URLs or use a radius/bounding box as the scope (iDNES, Reas, Bazoš all lost most listings that way). Check the pagination parameter on page 2 (Reas: `listPage`, not `page`).
- **Cheap two-phase runs** (iDNES, Realingo, Bazoš): all list pages first, `touch_listings` for known listings, details only for new ones and price changes, a 36-min time budget, and `lists_complete = False` → `mark_active_seen` so an incomplete crawl deactivates nothing. Realingo remembers offers taken over from portals we scrape directly in `re_realestate.scrape_skips`.
- **Reserved / sold**: reserved = keep, `price_note = "Rezervace"`, `keep_last_price = True`; sold or rented out = `deactivate_listing`. Take type, price and state from the site's structured fields, not from keyword order in the title.
- **robots.txt**: new sources are added only when robots.txt allows the crawl. Sreality (`Disallow: /`) and Bazoš (search parameters disallowed) are scraped anyway by the owner's decision – no official feed exists. `scraping.respect_robots_txt` in settings.yaml is not read by any code.
- Bazoš searches: Znojmo + 35 km (all categories) and Brno + 30 km by category; only list items whose district is Znojmo or Brno-venkov are fetched. Not covered on purpose: Bazoš and iDNES for Brno-město, rentals on RealityMIX / Reality Čechy / Bezrealitky / reality-znojmo.cz, Sreality category 5 ("ostatní").

**Partial district Břeclav (7. 10. 2026).** The scope is okres Znojmo, Brno-venkov and Brno-město in full, plus the Pálava / Nové Mlýny part of okres Břeclav only (Mikulov, Sedlec, Bavory, Perná, Klentnice, Pavlov, Milovice, Bulhary, Dolní and Horní Věstonice, Strachotín, Šakvice, Starovice, Pouzdřany, Popice, Uherčice, Velké Němčice, Dolní Dunajovice, Březí, Dobré Pole, Novosedly, Nový Přerov, Brod nad Dyjí, Jevišovka, Drnholec; deliberately not Hustopeče, Křepice, Nikolčice, Zaječí, Lednice, Valtice, Hlohovec). `search_filters.partial_districts` in settings.yaml lists them; `filters.py` accepts a listing that names one of them as a whole word (with or without diacritics, slug form too – "Pavlov" must not match "Velké Pavlovice") and rejects everything else that mentions the district, even when "jihomoravsk" would otherwise let it through. The scrapers crawl the whole okres Břeclav list (Reas, Sreality 74, iDNES `okres-breclav`, Bezrealitky R442309, RE/MAX 3704, Realingo `Okres_Břeclav` with diacritics, RealityMIX, Reality Čechy 20023704, M&M, Century 21, HV Reality, Bazoš Mikulov + 20 km) and the filter keeps only those villages; Realcity has no Břeclav list (Cloudflare blocks the lookup of its district id). `district_municipalities.py` carries the same villages under "Břeclav" for UlovDomov and iDNES.

**iDNES** (`IDNES`, rewritten 6. 10. 2026): crawls the per-district result lists `reality.idnes.cz/s/okres-znojmo/` and `/s/brno-venkov/` (`?page=N` from 0, 404 after the last page); municipality and district come from the list item ("Ulice, Obec, okres X"). Details are fetched only for new listings and price changes (max 800 per run and a 36-min time budget – a full nightly refetch of ~2 900 details would not fit the 45-min task limit, and an overrun is reported as a failed scraper); known listings are refreshed with `db_manager.touch_listings`. A list page that cannot be loaded marks the crawl incomplete and nothing is deactivated in that run. The detail price skips the struck-through `<del>` (pre-discount) price. Items the search filters would reject are not fetched. Until then the scraper matched 13 municipality names anywhere in sitemap URLs, which covered 370 of ~2 900 listings and let in Prague streets named after a "Miroslav".

**District** (`listings.district`): sources that do not send it (Reas, Prodejme.to, part of iDNES) get it after every scrape, before duplicate detection, from `db_manager.fill_missing_districts()` – point-in-polygon against `re_realestate.districts` (76 okres boundaries from OpenStreetMap, `scripts/migrate_districts.sql`; agrees with Sreality in 4 494 of 4 496 listings), and without GPS from a municipality → district dictionary built from Sreality (`core/district_lookup.py`, unambiguous municipalities only).

**Photo classification vs. re-uploaded photos** (since 6. 10. 2026): a classification belongs to the image URL it was made for. `_upsert_photos` never moves it to a new URL – brokers re-upload galleries (Sreality, Lexamo: every URL changes, often the order too) and the position-based transfer used from 1. 10. glued the living-room description to the pool photo. A new URL is inserted unclassified, the vanished classified row without a stored copy is deleted, and when a gallery that already had classified photos receives a new URL the runner asks the API after duplicate detection to classify the rest (`db_manager.pop_galleries_to_reclassify()` → `POST /api/photos/bulk-classify?listingId=&wait=false`).

**Scrape run history and drift alert** (since 7. 10. 2026): the runner writes one `scrape_runs` row per source and job (`record_scrape_run`, column `full_rescan` added lazily). After each job `notifications.detect_drops` compares every source's count with the median of its last 5 successful runs of the same mode; below 60 % (median ≥ 20) → Slack line „Propad výsledků“ even when nothing failed – a changed website usually returns a fraction, not zero. `count_data_anomalies` adds „Podezřelá data u nových inzerátů“ (missing municipality, house > 1 500 m², house < 3 000 or > 250 000 Kč/m², land > 20 ha) for listings first seen in that run.

**Inspection photos from phones** (since 7. 10. 2026): HEIC/HEIF is converted to JPEG on upload by `HeifConverter` (`heif-convert` from `libheif-examples` in the API image; when it fails the file is stored as received) in both `upload-inspection-photos` and `/api/listings/{id}/photos`; `TakenAt` comes from EXIF `DateTimeOriginal` (`ExifReader`, Europe/Prague → UTC) with upload time as fallback. Thumbnails and vision input already honour EXIF orientation (`ImageDownscaler`).

**Broker contact** (`listings.seller_name/_email/_phone/_company`, since 1. 10. 2026): filled by the Sreality scraper from the v1 detail (`user` + `premise`), by RealityMIX from the detail sidebar and by Reality Čechy without the phone (the portal reveals it only after a click); other sources leave it NULL and the upsert keeps what is known (`COALESCE`). `GET /api/listings/{id}` returns `seller*` **only to admins** (third-party personal data) and borrows the contact from a duplicate-group member when the opened listing has none (`sellerFromSourceCode`). MCP `get_listing` prints it as **Makléř**.

**Sreality detail URLs**: `/detail/{type}/{main}/{sub}/{locality}/{hash_id}`. Sreality 301-redirects to the canonical address whenever every segment is *some* valid slug, and returns 404 when a segment is missing or unknown. `_CAT_SUB_SLUG` holds the 48 sub-type slugs verified against live listings (auctions are `drazby`); an unknown code falls back to `rodinny` / locality `x` instead of dropping the segment. New sub-type code = look up the redirect of `/detail/prodej/dum/rodinny/x/<hash_id>` and add it.

### AI/RAG pipeline

1. `OllamaEmbeddingService` (or `OpenAIEmbeddingService`) generates 768-dim vectors.
2. Vectors stored in `listings.description_embedding` and `listing_analyses.embedding`.
3. `RagService` performs cosine similarity search via pgvector IVFFlat index.
4. `PhotoClassificationService` classifies listing photos into 13 categories with a cloud vision model: `google/gemini-3.1-flash-lite` via OpenRouter, falling back to Mistral (`mistral-medium-latest`). `PhotoDamageValidator` keeps `damage_detected` only when the model backed it with a damage label, `damage_evidence`, or the description, and never on a render (label `visualization`, set from the prompt's `is_visualization`; such photos get description "Vizualizace: …" and the analysis prompt marks them as not the real condition). Model choice comes from a 9-model benchmark (Sept 2026); local Ollama vision models were too slow (~20 s/photo).
5. **Inzerát vs. prohlídka** (`InspectionComparisonService`, od 6. 10. 2026): `POST /api/listings/{id}/compare-inspection` (úloha na pozadí) klasifikuje fotky z prohlídky, pak pošle fotky z inzerátu a z prohlídky téže kategorie společně obrazovému modelu (`IPhotoClassificationService.AskVisionAsync`, fotky se zmenšují přes `ImageDownscaler`/SkiaSharp). Nálezy (`hidden_defect`, `retouched`, `brightened`, `wide_angle`, `staged`, `outdated`, `visualization`, `omitted`) jdou do `listing_photo_comparisons`, zpráva do analýz (source `photo-comparison`); `GET /api/inspection-comparisons/summary` je sčítá napříč domy. MCP `compare_inspection_photos`, `get_inspection_findings`.
6. **Dvojčata v galerii** (`PhotoTwinService`): po klasifikaci celé galerie (a přes `POST /api/photos/detect-twins`) jdou fotky jedné kategorie modelu společně; dvojice se stejným záběrem a jiným interiérem dostanou štítek `twin`, upravená verze i `visualization`. MCP `detect_photo_twins`.
7. **Poloha domu** (`HousePositionService`, od 6. 10. 2026): `listings.house_position` = `detached` / `semi_detached` / `terraced` / `corner` / `unknown` (česky samostatný / přisazený z jedné strany / řadový / rohový, `HousePositions.Label`) + `house_position_reason`. Určuje se jedním dotazem obrazovému modelu: první jde katastrální mapa a ortofoto ČÚZK 200 × 200 m kolem přesné GPS domu (`CuzkMapService`, veřejné WMS `services.cuzk.cz/wms/wms.asp` – vrstvy `hranice_parcel,POL_BUDOV,parcelni_cisla` – a `ags.cuzk.gov.cz/arcgis1/services/ORTOFOTO/MapServer/WMSServer` vrstva `0`; červený zaměřovač uprostřed kreslí SkiaSharp; geokódovaný střed obce se nepoužije), pak až 4 venkovní fotky z galerie (letecké první, vizualizace ne). Bez map i leteckého snímku zdůvodnění začíná „Bez leteckého snímku i mapy – méně jisté.“ a zapíše se ke všem členům skupiny duplicit; běží po klasifikaci celé galerie a přes `POST /api/photos/detect-house-position?listingId=`. `house_position_listed` je tvrzení makléře ze Sreality (`object_kind`) – bývá nepřesné (Lechovice: „Řadový“ u domu s vjezdem), proto se zobrazuje zvlášť jako „Makléř uvádí“. MCP `detect_house_position`, `get_listing` vypisuje řádek „Poloha domu“. Hledání: `housePositions` (multi-select, dům bez určení vypadne) – filtr „Poloha domu“ na stránce inzerátů, v uloženém hledání i v MCP `search_listings(house_positions=["samostatný"])`; výsledky nesou `housePosition` / `housePositionLabel`.
8. Embedding provider is selected at startup: `Embedding:Provider=ollama` → Ollama, otherwise OpenAI.

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

**Since 7. 10. 2026 schema changes are SQL files in `scripts/migrate_*.sql`, applied automatically.** `SqlMigrationRunner` (Infrastructure) embeds every `scripts/migrate_*.sql` into the assembly (`COPY scripts/ /scripts/` in the API Dockerfile) and at API start runs each file that is not yet recorded in `re_realestate.schema_migrations`, inside a transaction, in file-name order. A database that already existed when the table was introduced gets all files of that moment recorded as `baseline = true` without running them (most of them were one-off data fixes applied by hand). Rules for a new migration: name it `migrate_YYYYMMDD_<topic>.sql`, write it idempotent anyway (`IF NOT EXISTS`), no `CONCURRENTLY` or other statements that cannot run in a transaction, a `-- KONTEXT:` header saying why. Do not put new DDL into `DbInitializer` constants any more (the existing ones stay as baseline) and do not create EF migrations – the `Migrations/` folder is frozen since 24. 2. 2026. `EnsureCreatedAsync` still creates the schema on an empty database; `SKIP_EF_MIGRATIONS=true` skips both. The scraper may still add its own small tables lazily (`scrape_skips`, `scrape_runs.full_rescan`).
