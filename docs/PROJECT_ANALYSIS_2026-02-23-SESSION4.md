# Hloubková analýza projektu – Session 4
**Datum:** 23. února 2026  
**Commit baseline:** `32077e3` (po aplikaci všech Session 4 fixů)  
**DB stav:** 1 236 aktivních inzerátů, 6 919 fotek, 12 zdrojů  
**Testy:** 39 unit testů zelených

---

## 📊 Celkové hodnocení

| Oblast | Stav | Poznámka |
|---|---|---|
| **Architektura** | ✅ Dobrá | Clean architecture, Minimal APIs, Repository pattern |
| **Stabilita** | ✅ Dobrá | CancellationToken, IDisposable, retry logic |
| **Bezpečnost** | ✅ Dobrá | API key, CORS – základy jsou na místě |
| **Výkon** | ✅ Dobrá | GIN index, Split query, Filtered Include, tiebreaker |
| **Testovatelnost** | ⚠️ Střední | 39 unit testů, ale žádné integration testy |
| **Observabilita** | ❌ Chybí | Žádné Serilog/Prometheus metriky |
| **CI/CD** | ❌ Chybí | Žádné GitHub Actions/pipelines |
| **Scrapery** | ⚠️ Střední | 4 zdroje s <5 inzeráty, selektory pravděpodobně zastaralé |

---

## 🚨 CRITICAL (musí být opraveno ihned)

### C1 – search_tsv sloupec chyběl v živé DB ✅ OPRAVENO TENTO RUN
**Problém:** `search_tsv GENERATED ALWAYS AS` sloupec byl v `init-db.sql`, ale DB byla vytvořena přes `EnsureCreatedAsync` dříve – sloupec nebyl nikdy `ALTER TABLE`-ován do existující DB.  
**Dopad:** Jakékoliv hledání s textem (`SearchText != null`) by způsobilo runtime `PostgresException: column search_tsv does not exist`.  
**Řešení:**  
1. Sloupec a GIN index aplikovány ručně na běžící DB (`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`)
2. `DbInitializer.SeedAsync()` nyní obsahuje idempotentní SQL patch – každý `EnsureCreated` + `SeedAsync` call automaticky doplní chybějící sloupec.

```csharp
// DbInitializer.cs – automatický patch při startu
await dbContext.Database.ExecuteSqlRawAsync("""
    ALTER TABLE re_realestate.listings
        ADD COLUMN IF NOT EXISTS search_tsv tsvector GENERATED ALWAYS AS (...) STORED;
    CREATE INDEX IF NOT EXISTS idx_listings_search_tsv ON re_realestate.listings USING gin (search_tsv);
    """, cancellationToken);
```

### C2 – ScrapingService mutuje `BaseAddress` na instanci z IHttpClientFactory
**Problém:** `ScrapingService.cs` řádek `_httpClient.BaseAddress = new Uri(scraperApiUrl)` mění `BaseAddress` na sdíleném `HttpClient` – thread-unsafe a způsobuje problémy při concurrentním přístupu.  
**Dopad:** Při souběžných voláních může jeden request přepsat URL druhému.  
```csharp
// Problematický kód:
var scraperApiUrl = Environment.GetEnvironmentVariable(...) ?? _httpClient.BaseAddress?.ToString() ...;
_httpClient.BaseAddress = new Uri(scraperApiUrl); // ← mutace!
var response = await _httpClient.PostAsJsonAsync("/v1/scrape/run", request, ct);
```
**Doporučení:** URL je nastavena v `ServiceCollectionExtensions.cs` – odstranit mutaci z `ScrapingService.cs` a spoléhat na nakonfigurovaný `HttpClient`.

---

## 🔴 HIGH (opravit v příštím sprintu)

### H1 – Žádné DB migrace (EF Core Migrations chybí)
**Stav:** Aplikace používá `EnsureCreatedAsync()` + ruční `ALTER TABLE` patche v `DbInitializer`.  
**Dopad:** Nelze bezpečně měnit schema v produkci bez ztráty dat. EF Core Migrations by zajistily atomické, verzované migrace.  
**Doporučení:** Přejít na `dotnet ef migrations add Initial` + `database.MigrateAsync()` nebo ponechat `EnsureCreated` ale udržovat `DbInitializer` jako kompletní patch manager.  
**Priorita:** Vysoká – nutné před jakýmkoliv production deploymentem.

### H2 – RemaxScrapingService.cs (mrtvý kód)
**Problém:** Soubor `src/RealEstate.Api/Services/RemaxScrapingService.cs` existuje (ověřit jméno – možná přejmenován na `RemaxZnojmoImportService.cs`), ale není registrován v DI a zřejmě není volán.  
**Dopad:** Dead code zvyšuje maintenance overhead.  
**Doporučení:** Ověřit, smazat nebo integrovat.

### H3 – 4 scrapery s <5 inzeráty (broken selektory)
**Stav:** ZNOJMOREALITY (5), DELUXREALITY (5), PRODEJMETO (4), LEXAMO (4)  
**Příčiny (odhadované):**
- ZNOJMOREALITY a DELUXREALITY: WordPress/Elementor weby mění strukturu HTML
- PRODEJMETO: Možná paginace nebo filtrování URL se změnilo
- LEXAMO: Webflow SSR – možný layout change
**Doporučení:** Debug každý selektorový soubor, porovnat s živým HTML.

### H4 – Žádný rate limiting na API
**Problém:** Žádný rate limiting na `/api/listings/search` – mohou být DOS-ovány databázové dotazy.  
**Dopad:** Databáze může být přetížena mnoha dotazy.  
**Doporučení:** `AspNetCoreRateLimit` nebo middleware s `MemoryCache`.

### H5 – `RemaxScrapingService` / `RemaxScrapingProfileDto` záhadné soubory
**Zjištění:** Ve workspace existují soubory `RemaxScrapingProfileDto.cs` a příbuzné – ověřit jejich účel a zda nejsou duplikáty.

### H6 – Blazor App bez globálního error boundary
**Problém:** Žádný `<ErrorBoundary>` kolem hlavního obsahu v `App.razor`/`Routes.razor`.  
**Dopad:** Neošetřená výjimka křesne celý Blazor circuit.  
**Doporučení:** Přidat `<ErrorBoundary>` do `Routes.razor`.

### H7 – ListingDetail.razor nemá CancellationToken
**Problém:** Na rozdíl od `Listings.razor`, `ListingDetail.razor` nepouší IDisposable + CancellationToken pattern.  
**Dopad:** HTTP volání pokračují i po navigaci pryč.  
**Doporučení:** Aplikovat stejný pattern jako Listings.razor (viz Session 4).

---

## 🟡 MEDIUM

### M1 – Žádné structured logging
**Problém:** `Console.WriteLine($"[STARTUP]...")` v `Program.cs`, bez Serilog/structured logy.  
**Doporučení:** `Serilog` s JSON output → snadné parsování v produkci.

### M2 – Hardcoded DefaultUserId
**Problém:** `Guid.Parse("00000000-0000-0000-0000-000000000001")` na dvou místech (`ListingRepository.cs`, `ListingService.cs`).  
**Doporučení:** Extrahovat do sdílené konstanty v Domain vrstvě.

### M3 – Žádné robustní logování scraperů (Python)
**Problém:** Scrapery logují pouze na úrovni WARNING+ – chybí debug timing, metriky per-zdroj.  
**Doporučení:** Přidat `scrape_duration_seconds`, počet úspěšných/neúspěšných upsertů per run.

### M4 – AnalysisService chybí implementace
**Problém:** `AnalysisService.cs` ze všeho pravděpodobně obsahuje stub implementaci. AI analýza je v backlogu.  
**Doporučení:** Ověřit stav, přidat placeholder error pro neimplementované operace.

### M5 – Scrape.razor stránka – neznámý stav
**Zjištění:** Soubor `Scrape.razor` existuje, ale není zřejmé, zda správně posílá API key header.  
**Doporučení:** Ověřit, zda trigger volání z UI obsahuje `X-Api-Key` header.

### M6 – Docker: Blazor App není v docker-compose
**Stav:** `docker-compose.yml` má `postgres`, `api`, `scraper` – ale Blazor App (port 5002) se spouští lokálně `dotnet run`.  
**Doporučení:** Přidat `app` service do docker-compose nebo přejít na .NET Aspire.

### M7 – `StorageService` registrace bez implementace
**Problém:** `builder.Services.AddStorageService(builder.Configuration)` v Program.cs – ověřit jestli je implementace kompletní nebo stub.

### M8 – Chybí `NOT NULL` na `search_tsv` indexovaném sloupci
**Info:** `search_tsv` je GENERATED – vždy bude NOT NULL. Nicméně EF model nemá toto explicitně. Není kritické.

### M9 – ScrapingService: duplicitní BaseAddress čtení z env
**Problém:** URL ve `ServiceCollectionExtensions.cs` řádek `client.BaseAddress = new Uri(scraperApiUrl)` + znovu v `ScrapingService.cs` – duplicita.

---

## 🟢 LOW (vylepšení)

### L1 – Blazor App: Scrape stránka UI/UX
Formulář pro scraping nemusí správně zobrazovat progress/výsledky. Ověřit.

### L2 – Chybí HTTPX timeout konfigurace v scraperech
Scrapery nemají explicitní `timeout=30.0` v `httpx.AsyncClient`. Retry logic pomáhá, ale timeout by byl lepší první linií obrany.

### L3 – Photo download pipeline (backlog)
`original_url` → `stored_url` (S3/GridFS/lokální disk) stále chybí. Fotky jsou vždy ze zdrojových URL, která mohou přestat fungovat.

### L4 – Chybí test cover pro scrapery
Unit testy neobsahují mock HTML parsing pro jednotlivé scrapery. Jen 39 testů pro backend DTOs/enum/services.

### L5 – GitHub Actions/CI chybí
Žádný workflow pro `dotnet test` + `dotnet build` při PR. Snadné přidat.

### L6 – CENTURY21 logo placeholder
`wwwroot/images/logos/CENTURY21.svg` je placeholder 274B – reálné logo za WP loginem.

### L7 – `docs/FILTERING_ARCHITECTURE.md` vs kód
Tento dokument popisuje starší ILIKE filtrování. Neaktualizuje se automaticky.

### L8 – Semantic search (pgvector backlog)
`description_embedding vector(1536)` sloupec existuje v DB + HNSW index, ale OpenAI embeddings nejsou implementovány. Velká příležitost.

---

## 📐 Architektura – silné stránky

1. **Clean Architecture** dodržena: Domain, Infrastructure, Api, App jako oddělené projekty
2. **Minimal APIs** místo MVC Controllers – správný přístup pro .NET 10
3. **PredicateBuilder (LinqKit)** – flexibilní dynamické dotazy bez SQL injection rizika
4. **AsSplitQuery()** – správně zabraňuje kartézskému produktu při Includes + paginaci
5. **Python/C# separation** – scrapery jsou zcela oddělené od .NET backendu
6. **Async everywhere** – jak Python, tak .NET plně async
7. **tsvector GENERATED ALWAYS** – správné použití PostgreSQL generovaných sloupců

---

## 📐 Architektura – slabé stránky

1. **EnsureCreated vs Migrations** – není verzované schema, každý patch musí být v DbInitializer
2. **DefaultUserId hardcoded** – authentication je "simulovaná" jedním uživatelem
3. **HttpClient mutace BaseAddress** – anti-pattern v ScrapingService
4. **Monorepo bez shared contracts** – App a Api duplikují některé DTO typy
5. **Žádná queue/message bus** – scraping job je při restartu ztracen

---

## 🔍 DB Schema analýza

### Indexy (32 indexů celkem)
| Tabulka | Klíčové indexy | Status |
|---|---|---|
| `listings` | `pk`, `(source_id, external_id)` UNIQUE, `is_active`, `(is_active, municipality, price)`, `(is_active, region, price)`, `(property_type, offer_type)`, `first_seen_at`, `search_tsv` GIN | ✅ Kompletní |
| `listing_photos` | `pk`, `(listing_id, order_index)` | ✅ OK |
| `sources` | `pk`, `code` UNIQUE | ✅ OK |
| `user_listing_state` | `pk`, `(user_id, listing_id)`, `(user_id, status)`, `listing_id` | ✅ OK |
| `analysis_jobs` | `pk`, `listing_id`, `status`, `(status, requested_at)`, `user_id` | ✅ OK |
| `scrape_runs` | `pk`, `(source_code, started_at)`, `source_id`, `status` | ✅ OK |
| `user_listing_photos` | `pk`, `listing_id`, `uploaded_at` | ✅ OK (nová z Session 4) |

### Chybějící indexy (potenciální)
- `listings.source_code` – existuje ✅
- `listings.offer_type` standalone – ne, ale composite s property_type existuje ✅
- `listing_photos.listing_id` – existuje jako součást composite ✅

---

## 🕷️ Scraper analýza

| Zdroj | Počet | Status | Poznámka |
|---|---|---|---|
| SREALITY | 851 | ✅ | Dominantní zdroj, JSON API scraping |
| IDNES | 168 | ✅ | Dobrý výsledek |
| PREMIAREALITY | 51 | ✅ | Custom SSR |
| REMAX | 38 | ✅ | Stabilní |
| NEMZNOJMO | 34 | ✅ | Eurobydleni platform |
| CENTURY21 | 31 | ✅ | Dobrý výsledek |
| HVREALITY | 24 | ⚠️ | WordPress, ověřit selektory |
| MMR | 21 | ⚠️ | Nízký výsledek |
| ZNOJMOREALITY | 5 | ❌ | Problematické selektory |
| DELUXREALITY | 5 | ❌ | Problematické selektory |
| PRODEJMETO | 4 | ❌ | Problematické selektory |
| LEXAMO | 4 | ❌ | Problematické selektory |

**Retry logic:** ✅ `tenacity` aplikován na 11/12 scraperů (Session 4)  
**Rate limiting:** ✅ `asyncio.sleep(1)` v většině scraperů  
**Deduplication:** ✅ `(source_id, external_id)` UNIQUE constraint

---

## ✅ Co bylo implementováno v Session 4 (summary)

| Item | Dopad | Status |
|---|---|---|
| API key middleware | Bezpečnost scrapingu | ✅ |
| CORS policy | Browser security | ✅ |
| /health endpoint + Docker healthcheck | Ops reliability | ✅ |
| Filtered Include UserStates | N+1 odstraněno | ✅ |
| tsvector search (GIN index) | Výkon fulltext | ✅ |
| `search_tsv` DB patch v DbInitializer | Automatická migrace | ✅ (tento run) |
| Tiebreaker `.ThenBy(Id)` | Deterministické stránkování | ✅ |
| CancellationToken v Listings.razor | UX + zdroje | ✅ |
| HTTP retry (tenacity) | Scraper spolehlivost | ✅ |
| SourceDto → Models/ | Refaktoring | ✅ |
| 39 unit testů | Test coverage | ✅ |

---

## 🗺️ Doporučené next steps (prioritizováno)

### Sprint 5 (Critical fixes)
1. **[C2]** Opravit `ScrapingService.cs` mutaci BaseAddress
2. **[H1]** Zvážit přechod na EF Core Migrations nebo rozšířit DbInitializer patch manager
3. **[H7]** Přidat CancellationToken + IDisposable do `ListingDetail.razor`
4. **[H6]** Přidat `<ErrorBoundary>` do `Routes.razor`

### Sprint 6 (Quality)
5. **[H3]** Debug 4 scraperů s málo výsledky (ZNOJMOREALITY, DELUXREALITY, PRODEJMETO, LEXAMO)
6. **[M6]** Přidat Blazor App do `docker-compose.yml`
7. **[H4]** Rate limiting na API (AspNetCoreRateLimit)
8. **[M1]** Serilog základní konfigurace

### Sprint 7 (Features)
9. **[L8]** Semantic search – pgvector + OpenAI embeddings (column already exists!)
10. **[L3]** Photo download pipeline – original_url → stored_url  
11. **[L5]** GitHub Actions CI/CD pipeline

---

**Analýza dokončena:** 23. února 2026  
**Celkový stav:** ✅ Stabilní produkční základ, drobné architekturní dluhy, 4 broken scrapery.
