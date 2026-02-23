# Hloubková analýza projektu – RealEstateAggregator

**Datum:** 23. února 2026  
**Stav:** 12 scraperů, 1 236 inzerátů, Docker stack plně funkční, commit `546a418`

---

## Souhrn dle Severity

| Severity | Počet | Klíčové položky |
|----------|-------|-----------------|
| **CRITICAL** | 2 | Žádná autentizace na API, zero test coverage |
| **HIGH** | 8 | Hardcoded credentials, N+1 UserStates, fulltext ignoruje GIN index, žádné health checky API/App, retry chybí v scraperech, EF migrace neexistují, CORS chybí, LoadCardsAsync bez CT |
| **MEDIUM** | 9 | ListingService závisí na DbContext, SourceDto inline, GetActiveListingsAsync bez paginace, pgAdmin hardcoded heslo, chybí IDNES logo, EnsureCreated skipnut v Production, tiebreaker v řazení, duplicitní scraper_url read, LoadCardsAsync duplicuje filter |
| **LOW** | 6 | DefaultConnection nepoužitý klíč, dead code (PlaywrightOrchestrator), UserListingPhoto bez SQL tabulky, redundantní StateHasChanged, zbytečný await Task.CompletedTask, placeholder test |

---

## CRITICAL

### C1 – Žádná autentizace ani autorizace na API
**Soubor:** [src/RealEstate.Api/Program.cs](src/RealEstate.Api/Program.cs)

`/api/scraping/trigger` je veřejně přístupné – kdokoli může spustit full rescan všech 12 scraperů.

```csharp
// ❌ Žádné UseAuthentication, UseAuthorization, RequireAuthorization
app.MapScrapingEndpoints(); // ← veřejně přístupné
```

**Doporučení:** Přidat API key middleware nebo JWT. Minimálně přidat IP whitelist nebo basic auth na scraping endpointy.

---

### C2 – Zero test coverage
**Soubor:** [tests/RealEstate.Tests/UnitTest1.cs](tests/RealEstate.Tests/UnitTest1.cs)

Celý test projekt je prázdný placeholder. Kritické oblasti bez testů:
- Enum konvertory (`PropertyType`, `OfferType` HasConversion)
- `BuildSearchPredicate` / `BuildBasePredicate`
- Scraper HTML parsing
- Upsert deduplikace logika

---

## HIGH

### H1 – Hardcoded credentials v git historii
**Soubory:** [src/RealEstate.Api/appsettings.Development.json](src/RealEstate.Api/appsettings.Development.json), [docker-compose.yml](docker-compose.yml)

```json
"RealEstate": "Host=localhost;Password=dev"  // v git
```
```yaml
PGADMIN_DEFAULT_PASSWORD: admin  // v git
```

**Doporučení:** Přidat `appsettings.Development.json` do `.gitignore`. Docker secrets nebo `.env` soubor.

---

### H2 – N+1: `UserStates` načítán bez Filtered Include
**Soubor:** [src/RealEstate.Infrastructure/Repositories/ListingRepository.cs](src/RealEstate.Infrastructure/Repositories/ListingRepository.cs#L22)

```csharp
.Include(l => l.UserStates)  // ❌ načte VŠECHNY user states, filtruje se v C#
```

MapToSummaryDto pak: `entity.UserStates.FirstOrDefault(s => s.UserId == DefaultUserId)`

**Fix (EF Core 5+):**
```csharp
.Include(l => l.UserStates.Where(s => s.UserId == defaultUserId))
```

---

### H3 – Fulltext search ignoruje GIN tsvector index
**Soubor:** [src/RealEstate.Api/Services/ListingService.cs](src/RealEstate.Api/Services/ListingService.cs#L338)

```csharp
x.Title.ToLower().Contains(temp)  // ❌ ILIKE '%keyword%' = full table scan
```

DB má krásně připravený `search_tsv` GIN index s tsvector, ale vůbec se nepoužívá.

**Fix:**
```csharp
// EF Core s Npgsql:
.Where(x => EF.Functions.ToTsVector("simple", x.Title).Matches(query))
// nebo raw SQL:
// WHERE search_tsv @@ plainto_tsquery('simple', @query)
```

---

### H4 – API a App container nemají health check
**Soubor:** [docker-compose.yml](docker-compose.yml)

`app` závisí na `api` pouze přes `depends_on: - api` (start, ne zdravá odpověď).

**Fix:**
```yaml
api:
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
    interval: 10s
    retries: 5
    start_period: 30s

app:
  depends_on:
    api:
      condition: service_healthy
```

Nutné přidat `/health` endpoint v `Program.cs`:
```csharp
app.MapGet("/health", () => Results.Ok("healthy")).AllowAnonymous();
```

---

### H5 – Retry logika v scraperech neexistuje
**Soubory:** `scraper/core/scrapers/*.py`

`response.raise_for_status()` při 429 nebo 503 ukončí celý scrape bez retry.

**Fix:** `tenacity` library:
```python
from tenacity import retry, stop_after_attempt, wait_exponential

@retry(stop=stop_after_attempt(3), wait=wait_exponential(multiplier=1, min=2, max=10))
async def _fetch_page_http(self, url: str) -> str:
    ...
```

---

### H6 – EF Core migrace fakticky neexistují
**Soubor:** [src/RealEstate.Api/Program.cs](src/RealEstate.Api/Program.cs#L55)

```csharp
// "Use EnsureCreatedAsync instead of MigrateAsync to avoid column naming conflicts"
await dbContext.Database.EnsureCreatedAsync();
```

`EnsureCreatedAsync` vytvoří schéma od nuly ale **neumí aplikovat změny** na existující DB.  
Jakákoli změna schématu = ruční SQL nebo `docker-compose down -v` (ztráta dat).

---

### H7 – CORS konfigurace chybí
**Soubor:** [src/RealEstate.Api/Program.cs](src/RealEstate.Api/Program.cs)

`AddCors`/`UseCors` nikde. Nutné pokud bude API exponováno mimo Blazor Server.

---

### H8 – `LoadCardsAsync` bez CancellationToken
**Soubor:** [src/RealEstate.App/Components/Pages/Listings.razor](src/RealEstate.App/Components/Pages/Listings.razor#L490)

```csharp
var response = await Http.PostAsJsonAsync("api/listings/search", cardFilter);  // ❌ bez CT
```

`LoadServerData` správně předává `CancellationToken`. Při rychlém přepínání stránek mohou dobíhat staré requesty.

---

## MEDIUM

### M1 – `ListingService` závisí přímo na `RealEstateDbContext`
Api projekt závisí na Infrastructure – porušuje vrstvenou architekturu.

**Fix:** Přesunout UserStates/AnalysisJobs do repository vrtivy nebo přidat `IUserStateRepository`.

---

### M2 – `SourceDto` definován inline v Razor komponentě
```csharp
// Listings.razor line ~620
private record SourceDto(Guid Id, string Code, string Name, string BaseUrl, bool IsActive);
```
Patří do `Contracts/` nebo sdíleného projektu. Duplicita při přidání dalších komponent.

---

### M3 – Chybí IDNES logo (2. největší zdroj, 168 inzerátů)
**Soubor:** [src/RealEstate.App/Components/Pages/Listings.razor](src/RealEstate.App/Components/Pages/Listings.razor)

`_sourceLogoMap` nemá záznam pro `"IDNES"` → zobrazuje se fallback text chip místo loga.

---

### M4 – `GetActiveListingsAsync` bez stránkování
```csharp
return await query.ToListAsync(ct);  // načte VŠE do paměti
```
Při 10 000+ inzerátech paměťově nákladné.

---

### M5 – Chybí tiebreaker v řazení (nedeterministický AsSplitQuery)
`OrderByDescending(x => x.FirstSeenAt).ThenBy(x => x.Price)` – při shodě `Price` je pořadí nedeterministické.  
**Fix:** přidat `.ThenBy(x => x.Id)`.

---

### M6 – `EnsureCreatedAsync` skipnut v Production environment
V Dockeru `ASPNETCORE_ENVIRONMENT=Production` → blok s `EnsureCreatedAsync` se nevolá  
→ DB schéma závisí výhradně na `init-db.sql` při prvním startu.

---

## LOW

- **appsettings.json** má klíč `"DefaultConnection"` (nikdy se nepoužije) místo `"RealEstate"`
- **PlaywrightScrapingOrchestrator.cs** + **RemaxScrapingService.cs** = pravděpodobně dead code (scraping je delegován na Python)
- **UserListingPhoto** DbSet v kontextu, ale tabulka `user_listing_photos` není v `init-db.sql`
- **Redundantní `StateHasChanged()`** před a po await v `LoadCardsAsync`
- **`NavigateToDetailAsync`** = zbytečný async wrapper s `await Task.CompletedTask`
- **Duplicitní čtení `SCRAPER_API_BASE_URL`**: Program.cs zapisuje do Configuration, ServiceCollectionExtensions čte znovu z env

---

## Chybějící P0/P1 Features

| Priorita | Feature | Poznámka |
|----------|---------|----------|
| **P0** | Autentizace uživatelů | API naprosto otevřené |
| **P0** | EF Core migrace (místo EnsureCreated) | Schéma nelze evolovat bez ztráty dat |
| **P0** | CI/CD pipeline (GitHub Actions) | Žádný automated build/test |
| **P1** | Photo download pipeline | `stored_url` vždy null, fotky z cizích CDN |
| **P1** | Semantic search (pgvector + OpenAI embeddings) | Sloupec `embedding` existuje, embeddings ne |
| **P1** | Background scheduled scraping | Pouze ruční trigger, žádný cron |
| **P1** | Retry/backoff v scraperech | Všechny scrapery bez retry |
| **P1** | Unit testy | Zero coverage |
| **P1** | Health check endpoint + Docker integration | `/health` endpoint chybí |

---

*Vygenerováno: 23. února 2026 — GitHub Copilot hloubková analýza*
