# Hloubková analýza projektu – 25. srpna 2026

Navazuje na `PROJECT_ANALYSIS_2026-05-25.md`.

**Metodika:** statická analýza kódu, běh testů (C# + pytest), audit `docker-compose.yml`,
rozbor infrastruktury v repozitáři `zupagate`, **ověření živého stavu na produkci sudgate**
(SSH + veřejné HTTP sondy).

> **Poznámka k rozsahu:** RealEstate aplikace neběží lokálně — je nasazená na domácím serveru
> **sudgate** (`192.168.11.2`, vyřazený notebook s Dockerem). Infrastruktura je verzovaná
> v samostatném repu `/Volumes/edata/dev/zupagate`, na který tento repozitář **nikde neodkazuje**.
> Analýza vychází z živé produkce, ne z lokálního Dockeru.

---

## 0. Skutečný stav – ověřeno na produkci

**sudgate**, uptime 3 dny 15 h, load 0,11. Všech 17 kontejnerů běží, realestate stack `healthy`:

```
realestate-api    Up 3 days (healthy)     traefik      Up 3 days
realestate-app    Up 3 days (healthy)     prometheus   Up 3 days
realestate-db     Up 3 days (healthy)     grafana      Up 3 days
realestate-scraper Up 3 days              alertmanager Up 3 days
realestate-mcp    Up 3 days               node-exporter/cadvisor
```

| Metrika | Dokumentace tvrdí | **Skutečnost (25. 8.)** |
|---|---|---|
| Aktivní inzeráty | 1 558 | **2 249** |
| Celkem inzerátů | – | **6 803** |
| Funkční zdroje | 14 | **13 ze 14** (LEXAMO mrtvý) |
| C# testy | 79 | **141** |
| Python testy | 97 | **100** |
| MCP nástroje | 14 (CLAUDE.md) | **15** |
| Zdroje | 12 (CLAUDE.md) | **14** |

Rozložení podle zdrojů: SREALITY 1447 · BAZOS 299 · IDNES 204 · CENTURY21 77 · REMAX 45 ·
NEMZNOJMO 44 · PRODEJMETO 40 · PREMIAREALITY 27 · MMR 24 · REAS 21 · DELUXREALITY 10 ·
HVREALITY 7 · ZNOJMOREALITY 4 · **LEXAMO 0**.

---

## 1. TL;DR – top 8

| # | Problém | Závažnost | Obtížnost |
|---|---|---|---|
| 1 | **Celé API je na internetu bez autentizace** – `https://realestate.sudata.eu/api/*` vrací 200 bez jakéhokoli tokenu, včetně zápisových endpointů a 1GB uploadu | 🔴 Kritická | Malá (BasicAuth v Traefiku) |
| 2 | **LEXAMO scraper je 6,6 dne mrtvý** a nikdo se to nedozvěděl – `/v1/health/scrapers` hlásí `degraded`, ale Prometheus ten endpoint nescrapuje | 🔴 Vysoká | Malá |
| 3 | **Stack nelze postavit od nuly** – dvě chyby (viz kap. 3) znemožňují vytvoření čisté DB. Na vyřazeném notebooku bez zálohy DB je to reálné DR riziko | 🟠 Vysoká | Triviální |
| 4 | Produkce běží jako `Development` → pojistka proti výchozímu API klíči `dev-key-change-me` je **mrtvý kód**, `EnsureCreatedAsync` běží při každém startu | 🟠 Vysoká | Malá |
| 5 | Zranitelné balíčky: `AutoMapper 12.0.1` (high) – **nikde se nepoužívá**; `Microsoft.OpenApi 2.4.1` (high) | 🟠 Střední | Triviální |
| 6 | Tři nekonzistentní zdroje pravdy o schématu (EF migrace zmrzlé v únoru, `DbInitializer`, `scripts/migrate_*.sql`) – systémová příčina #3 | 🟠 Střední | Střední |
| 7 | Žádné CI – 241 testů se nespustí automaticky před deployem | 🟡 Střední | Malá |
| 8 | `DEPLOYMENT.md` popisuje neexistující nasazení; sudgate/Traefik/zupagate nejsou v tomto repu zmíněné **ani jednou** | 🟡 Střední | Malá |

---

## 2. Nejzávažnější: veřejné API bez autentizace 🔴

### 2.1 Ověřeno sondami zvenčí

```
GET  https://realestate.sudata.eu/api/sources          → 200 ✅ bez tokenu
GET  https://realestate.sudata.eu/api/listings/stats   → 200 ✅ bez tokenu
POST https://realestate.sudata.eu/api/scraping/trigger → 401 ✅ (X-Api-Key funguje)
GET  https://realestate.sudata.eu/swagger/index.html   → 404 ✅ (viz 2.3)
```

`config/traefik/dynamic/routes.yml` v zupagate směruje `Host(realestate.sudata.eu) && PathPrefix(/api)`
na `realestate-api:8080` s prioritou 100 a **bez jediného middleware**. Pro srovnání —
Prometheus i Alertmanager mají `middlewares: [monitoring-auth]`. API ho nemá.

Z 69 endpointů je API klíčem chráněno **5** (`/api/scraping/*`). Zbývajících 64 je veřejných:

| Endpoint | Co umožní anonymnímu návštěvníkovi |
|---|---|
| `POST /api/listings/{id}/state` | Přepsat vaše hodnocení líbí/nelíbí/navštívit |
| `POST /api/listings/deactivate-dead` | Hromadně deaktivovat inzeráty + vyvolat HEAD requesty na cizí weby z vaší IP |
| `POST /api/listings/{id}/photos` | Upload s limitem těla **1 GB** (`Program.cs:70`) → zaplnění disku notebooku |
| `POST /api/rag/ask` | Ollama inference na vašem HW (rate limit 20/min/IP, ale 20 LLM dotazů/min notebook položí) |
| `POST /api/rag/embed` | Embedding batch (5/min/IP) |
| `POST /api/export/drive/*` | Export do **vašeho** Google Drive přes váš OAuth token |
| `GET /api/listings/search` | `PageSize` bez horní meze (viz 6.2) |

Rate limiting je per-IP fixed window bez fronty — proti jednomu klientovi pomůže, proti
distribuovanému nebo jen otrávenému návštěvníkovi ne.

### 2.2 Oprava — 10 minut práce

Do `routes.yml` přidat middleware jen pro API router (frontend zůstane veřejný):

```yaml
middlewares:
  realestate-auth:
    basicAuth:
      users:
        - "petr:$$2b$$12$$…"        # htpasswd -nb petr 'heslo' | sed 's/\$/$$/g'

routers:
  realestate-api:
    rule: "Host(`realestate.sudata.eu`) && PathPrefix(`/api`)"
    middlewares: [realestate-auth]   # ← přidat
```

Blazor frontend volá API interně přes `ApiBaseUrl=http://realestate-api:8080` (Docker network),
takže BasicAuth na Traefiku ho **nerozbije**. Ověřit jen `ApiPublicUrl` — používá se pro
URL fotek v prohlížeči; fotky se ale servírují z `realestate-app` (`uploads_data:ro`), takže
by měly zůstat funkční. Otestovat po nasazení.

Alternativa, pokud má API zůstat veřejné: povolit anonymně jen `GET` a `POST /api/listings/search`,
zbytek za klíč.

### 2.3 Swagger — náhodně chráněný, ne záměrně

Ověřeno 404. Ne proto, že by byl vypnutý (`ASPNETCORE_ENVIRONMENT=Development` ho zapíná),
ale protože Traefik routuje na API jen `PathPrefix(/api)` a `/swagger` spadne na Blazor.
**Je to náhoda, ne obrana** — jakmile někdo přidá catch-all routu nebo změní PathPrefix,
Swagger se scraping endpointy je venku. Vypnout Swagger mimo Development.

### 2.4 Co naopak drží

- ✅ `POST /api/scraping/trigger` bez klíče → **401**.
- ✅ UFW + `DOCKER-USER` chain blokují přímé Docker porty z internetu (jen localhost +
  `192.168.11.0/24` + Docker subnety). Publikovaný port MCP `8002:8002` na `0.0.0.0` je tím
  **mitigovaný** — z internetu nedostupný, z LAN ano.
- ✅ TLS všude, 80→443 redirect, Let's Encrypt přes DNS-01.

---

## 3. LEXAMO je 6,6 dne mrtvý a nikdo to neví 🔴

Vlastní health endpoint aplikace to hlásí přesně:

```json
{"overall":"degraded","total_sources":14,"ok":13,"stale_or_dead":1,
 "sources":[{"code":"LEXAMO","active_count":0,"inactive_count":7,
             "last_seen_at":"2026-08-19T01:00:14Z","days_stale":6.6,"status":"dead"}, …]}
```

Ostatní zdroje mají `days_stale: 0.3` — noční scrape v 3:00 běží správně.
Data ukazují, že LEXAMO nikdy nebyl silný zdroj (7 inzerátů celkem), ale i tak: **zdroj umřel
19. srpna a systém, který má na to monitoring endpoint, alerting a Slack integraci, mlčel.**

### Příčina mezery v monitoringu

`prometheus.yml` scrapuje čtyři targety: `prometheus`, `node`, `cadvisor`, `traefik`.
Alerty v `alerts/docker.yml` hlídají `ContainerDown`, `ContainerOomKilled`, `ContainerHighCpu`,
`ContainerHighMemory`, `ContainerRestarting`.

Všechno jsou to **infrastrukturní** metriky. Kontejner `realestate-scraper` běží a je zdravý —
z pohledu cAdvisoru je všechno v pořádku. To, že jeden ze 14 parserů přestal vracet data,
je aplikační stav, který žádný z existujících alertů nevidí.

Přitom aplikace ten signál už produkuje — jen ho nikdo nesbírá:
- `GET :8001/v1/health/scrapers` → `overall: degraded`
- `GET :8080/health/db`, `/health/ollama`, `/health/scraper` na API
- `SLACK_WEBHOOK_URL` ve scraperu (posílá jen chyby při běhu, ne „zdroj tiše vysychá")

### Oprava

1. **Rychlá (dnes):** Prometheus blackbox nebo textfile exporter na `/v1/health/scrapers`
   + alert `ScraperSourceDead` na `stale_or_dead > 0` po dobu 6 h. Alertmanager už do Slacku umí.
2. **Opravit LEXAMO** — parser (`scraper/core/scrapers/lexamo_scraper.py`, 295 ř.) nejspíš narazil
   na změnu HTML. Diagnostika: `make scrape SOURCES=LEXAMO` a podívat se do logu scraperu.
3. Zvážit `/metrics` endpoint přímo v .NET API (`prometheus-net`) — počty inzerátů, stáří
   posledního scrape, hloubka fronty.

---

## 4. Stack nelze postavit od nuly 🟠

Původně jsem tohle označil za „kritický výpadek" — ve skutečnosti je lokální Docker
**záměrně opuštěný** ve prospěch sudgate. Chyby jsou ale reálné a mění se z „všechno je
rozbité" na **riziko obnovy po havárii**, což na vyřazeném notebooku není akademická otázka.

### 4.1 Dvě nezávislé příčiny

**A — špatný mount initdb.** `docker-compose.yml:17` mountuje celý `./scripts` jako
`/docker-entrypoint-initdb.d`. Postgres pouští `.sql` abecedně s `ON_ERROR_STOP=1`:
`backfill_condition.sql` běží **před** `init-db.sql`, spadne na neexistující tabulce a init
se přeruší. Doloženo v logu lokální DB:
```
running /docker-entrypoint-initdb.d/backfill_condition.sql
ERROR: relation "re_realestate.listings" does not exist    ← init končí zde
```
Adresář navíc obsahuje `.py` a `.sh` soubory, které tam nemají co dělat.

**B — chybějící PostGIS v EF modelu.** `RealEstateDbContext.cs:34` registruje jen `vector`,
přestože `SpatialArea.GeomWkt` je mapovaná na `HasColumnType("geometry")` (`:392`).
`EnsureCreatedAsync()` proto na čisté DB vždy selže: `42704: type "geometry" does not exist`.

Na sudgate se to neprojevuje, protože tamní DB vznikla dřív a `EnsureCreatedAsync` na
existující databázi nic nedělá. **Ale znamená to, že produkční DB nemá reprodukovatelný původ.**

### 4.2 Proč na tom záleží

Server je vyřazený notebook. Pokud odejde SSD:
- V repu **není** funkční cesta, jak schéma znovu postavit (obě větve — initdb i EF — jsou rozbité).
- 6 803 inzerátů včetně ručních hodnocení, poznámek, analýz a embeddingů.
- **Neověřil jsem existenci zálohy DB** — v zupagate ani v tomto repu není žádný backup skript
  ani cron. To je pravděpodobně největší nepokryté riziko celého projektu.

### 4.3 Oprava

```csharp
// RealEstateDbContext.OnModelCreating
modelBuilder.HasPostgresExtension("vector");
modelBuilder.HasPostgresExtension("postgis");     // ← přidat
modelBuilder.HasPostgresExtension("uuid-ossp");   // ← přidat
```

```yaml
# docker-compose.yml
- ./docker/initdb:/docker-entrypoint-initdb.d:ro   # jen CREATE EXTENSION + CREATE SCHEMA
```

Plus: **pg_dump cron na sudgate** s rotací a kopií mimo notebook, a jednou za čas ověřit,
že se dump nahraje do prázdné DB.

---

## 5. Konfigurace produkce 🟠

### 5.1 Development v produkci
`docker-compose.yml:37` nastavuje `ASPNETCORE_ENVIRONMENT=Development` a stejný compose se
používá na sudgate (`make deploy-api`). Důsledky:

1. **Pojistka na API klíč je mrtvý kód** (`Program.cs:85`):
   ```csharp
   if (builder.Environment.IsProduction() && apiKey == "dev-key-change-me")
       throw new InvalidOperationException(...);
   ```
   Prostředí nikdy není `Production`, takže se kontrola nespustí. Compose má
   `API_KEY=${API_KEY:-dev-key-change-me}` — pokud proměnná na sudgate chybí, běží produkce
   s klíčem zapsaným v gitu a nic to nenahlásí.
   **→ Ověř na sudgate:** `docker inspect realestate-api --format '{{range .Config.Env}}{{println .}}{{end}}' | grep API_KEY`
   (tenhle příkaz mi bezpečnostní filtr nedovolil spustit, musíš ho pustit sám.)
2. `EnsureCreatedAsync()` + `DbInitializer.SeedAsync()` běží při **každém** startu produkce.
3. Swagger zapnutý (dnes nedostupný jen díky routování, viz 2.3).

**Oprava:** `docker-compose.prod.yml` override s `ASPNETCORE_ENVIRONMENT=Production` a guard
na klíč přepsat tak, aby platil všude mimo Development.

### 5.2 Zranitelné závislosti
```
NU1903: AutoMapper 12.0.1        – high severity (GHSA-rvv3-g6hj-g44x)
NU1903: Microsoft.OpenApi 2.4.1  – high severity (GHSA-v5pm-xwqc-g5wc)
```
`AutoMapper.Extensions.Microsoft.DependencyInjection` (`RealEstate.Api.csproj:43`) se
**v žádném `.cs` souboru nepoužívá** — `CLAUDE.md` navíc explicitně říká „never AutoMapper".
Smazání reference = jedna high-severity CVE pryč bez jakéhokoli rizika.

### 5.3 Duplikované secrets
`secrets/` i `src/RealEstate.Api/secrets/` obsahují stejné `google-drive-sa.json` a
`google-drive-token.json`. OAuth token se při refreshi zapisuje jen do jednoho → tiché rozjetí
(commit `43182c5` „detect expired Google Drive token" ukazuje, že se to už projevilo).

Vedlejší nález mimo tento repo: `zupagate/hetzner_cred.txt` leží v gitovém repu.
`.gitignore` v zupagate má 99 bajtů — ověř, že ho pokrývá.

---

## 6. Kód 🟡

### 6.1 Správa schématu — tři pravdy

| Mechanismus | Stav | Poslední změna |
|---|---|---|
| EF migrace `Migrations/` | 4 migrace, zmrzlé | 24. 2. 2026 |
| `DbInitializer.cs` | 12× ALTER/CREATE ručně | průběžně |
| `scripts/migrate_*.sql` | 8 souborů, ručně přes `make db` | 25. 5. 2026 |

EF migrace v gitu neodpovídají skutečnému schématu (chybí cadastre, price_history,
photo_classification, PostGIS, dedup). Tohle je systémová příčina kapitoly 4.
**Doporučení:** squash migrace ze současného modelu ověřená proti produkčnímu schématu,
přepnout na `MigrateAsync()`, `DbInitializer` omezit na seed `sources`, staré SQL do `scripts/legacy/`.

### 6.2 Konkrétní nálezy

| Místo | Nález |
|---|---|
| `ListingRepository.cs:19-27` | `Query()` nemá `AsNoTracking()` — porušuje `CLAUDE.md`; CSV export načte 5 000 sledovaných entit |
| `ListingFilterDto.cs:49` | `PageSize` bez horní meze — veřejný `POST /api/listings/search` přijme `PageSize=1000000` (komentář v kódu tvrdí opak). V kombinaci s kap. 2 je to DoS vektor |
| `ScrapingPlaywrightEndpoints.cs` | `MapScrapingPlaywrightEndpoints()` definované, ale nikdy nezavolané — mrtvý kód (a kdyby se zavolalo, bylo by bez API klíče) |
| `Program.cs:243` | Porovnání API klíče není konstantní v čase (timing side-channel, nízké riziko) |
| `AnalysisService.cs:10` | `// TODO: Implement…` — celá entita `AnalysisJob` je mrtvá (květnový nález, stále otevřený) |
| `StorageServiceCollectionExtensions.cs:25` | `// TODO: Implement GoogleDriveStorageService` — nedokončená abstrakce |
| `UserPhotoEndpoints.cs:70,82` | HEIC→JPEG a EXIF stále TODO |
| `GoogleDriveExportService.cs:307` | `GoogleCredential.FromJson` deprecated kvůli bezpečnostnímu riziku |
| `mcp/server.py:288-299` | Mrtvá OneDrive větev (`hasOneDriveExport` už API nevrací) |
| `RealEstate.Api.csproj` | `EnableDefaultItems=false` + ruční `<Compile Include>` globy → nová podsložka se **tiše nezkompiluje** |
| `RealEstate.Tests.csproj` | MSB3277 — konflikt `EFCore.Relational` 10.0.1 vs 10.0.3 |

### 6.3 Duplikace ve scraperech
14 scraperů, **žádná společná bázová třída**. Každý znovu implementuje `_fetch`,
`_extract_price`/`_parse_price`, `_extract_photos`, `_extract_description`, `_extract_location`.
Odhad 1 500–2 000 řádků duplikátu. `database.py` sdílený upsert má — chybí sdílený *parsing*.
Riziko: oprava typu `357e927` („retry only 429/5xx") se aplikuje jen do jednoho souboru.

### 6.4 Blazor
`ListingDetail.razor` 2 446 ř. (z 2 825 v květnu), `Listings.razor` 1 125 ř., `Map.razor` 700 ř.
Extrakce běží (`SectionCard`, `PriceHistorySection`), není dokončená.
`DecisionReport.razor` (522 ř.) vypadá jako jednorázový nástroj — ověřit využití.

### 6.5 Úklid
2,9 GB v pracovním adresáři, z toho 1,4 GB `bin/` včetně rekurzivně vnořených
`bin/Release/net10.0/bin/Debug/net10.0/bin/Debug/…` (100+ úrovní, láme `grep -r`).
363 MB osiřelých `bin`+`obj` po smazaném projektu `RealEstate.Background`.
V gitu nepatří: `_test_*.py` (3), `benchmark_qwen_*.json` (2), `exports/` (11 souborů), `docs/*.docx` (2).

---

## 7. Dokumentace 🟡

### 7.1 Největší mezera: neexistuje most mezi repy

`DEPLOYMENT.md` je z 22. 2. 2026, verze 1.0.0, vyžaduje „.NET SDK 9.0+" (projekt je na .NET 10)
a popisuje `docker compose up` na localhostu. **Slova „sudgate", „Traefik" ani „zupagate" se
v celém repozitáři nevyskytují ani jednou.** Skutečné nasazení — reverse proxy, TLS přes
Wedos DNS-01, DDNS relay přes Hetzner, monitoring stack, firewall — je zdokumentované
v jiném repu, o kterém se tenhle nedozví.

Zároveň `CLAUDE.md` i `AGENTS.md` prezentují `make up` jako „primary workflow", ačkoli
lokální stack je opuštěný a rozbitý. Kdokoli (člověk i AI agent) podle nich začne
a narazí na hodinu ladění mrtvé konfigurace — přesně jak se stalo při této analýze.

**Oprava:** do `README.md`/`CLAUDE.md` sekci „Kde to běží" s odkazem na zupagate,
`DEPLOYMENT.md` přepsat na realitu (nebo nahradit odkazem), a jasně označit, jaký je stav
lokálního Dockeru.

### 7.2 Nepřesná tvrzení

| Tvrzení | Kde | Realita |
|---|---|---|
| „Cloud export – Google Drive + **OneDrive**" | `README.md` | OneDrive smazán v květnu (`b7d7596`) |
| „12 sources" / „14 tools" | `CLAUDE.md` | 14 zdrojů / 15 MCP nástrojů |
| „79 tests" / „97 pytest tests" | `AGENTS.md` | 141 / 100 |
| „~1 558 aktivních inzerátů" | README, AGENTS | 2 249 |
| „Docker stack plně funkční" | `README.md` | lokálně nefunkční, běží jen sudgate |
| „.NET SDK 9.0+" | `DEPLOYMENT.md` | .NET 10 |
| Tabulka env proměnných | `CLAUDE.md` | chybí `OpenRouter__`, `Groq__`, `Mistral__`, `Anthropic__`, `OllamaCloud__`, `SLACK_WEBHOOK_URL`, `PUBLIC_API_URL` |
| `PHOTOS_PUBLIC_BASE_URL` vs `PHOTOS_BASE_URL` | `CLAUDE.md` vs `0294933` | dvě jména, ověřit platné |

### 7.3 Sprawl
~450 kB v `docs/`, 6 překrývajících se analýz (`PROJECT_ANALYSIS.md`, `…02-23`, `…02-23-SESSION4`,
`…05-25`, `AI_SESSION_SUMMARY.md` 38 kB, tento dokument). `BACKLOG.md` (42 kB) míchá hotové
sprinty 0–9 s otevřenými položkami a má **dva** „Sprint 8". Archivovat do `docs/archive/`.

---

## 8. Plán

### Fáze 1 – Zavřít díry (½ dne) 🔴
1. BasicAuth middleware na `realestate-api` router v `zupagate/config/traefik/dynamic/routes.yml`,
   nasadit na sudgate, ověřit že frontend i fotky fungují.
2. Ověřit `API_KEY` na sudgate (příkaz v 5.1) — pokud je default, změnit.
3. `PageSize` clamp (max 200 search / 5 000 export).
4. Swagger vypnout mimo Development.

**Metrika:** `curl https://realestate.sudata.eu/api/sources` → 401.

### Fáze 2 – Vidět, když něco umře (½ dne) 🔴
1. Prometheus scrape `/v1/health/scrapers` + alert `ScraperSourceDead` (`stale_or_dead > 0` po 6 h) → Slack.
2. Opravit LEXAMO parser.
3. Přidat `/health/*` API endpointy jako blackbox targety.

**Metrika:** `overall: ok`, umělé shození zdroje vyvolá Slack alert do 6 h.

### Fáze 3 – Přežít smrt disku (½ dne) 🟠
1. `pg_dump` cron na sudgate + rotace + kopie mimo notebook.
2. **Otestovat obnovu** — dump do prázdné DB (odhalí i to, jestli Fáze 4 stačila).

**Metrika:** existuje dump mladší 24 h a je ověřeně obnovitelný.

### Fáze 4 – Reprodukovatelné schéma (1–2 dny) 🟠
1. `HasPostgresExtension("postgis")` + `("uuid-ossp")`.
2. `docker/initdb/00-extensions.sql`, přemountovat.
3. Squash EF migrace proti produkčnímu schématu, přepnout na `MigrateAsync()`.
4. `DbInitializer` osekat na seed; staré SQL do `scripts/legacy/`.

**Metrika:** čistá DB → `make up` → funkční schéma bez ručního SQL.

### Fáze 5 – Hygiena (1 den) 🟠
Odstranit `AutoMapper`, povýšit `Microsoft.OpenApi`, `docker-compose.prod.yml` s `Production`,
sjednotit `secrets/`, `AsNoTracking()`, smazat `ScrapingPlaywrightEndpoints` a OneDrive větev v MCP.
CI (`.github/workflows/ci.yml`): build + `dotnet test` + `pytest` + `dotnet list package --vulnerable`.

**Metrika:** build bez NU1903, zelené CI na PR.

### Fáze 6 – Dokumentace (1 den) 🟡
Most na zupagate, přepsat `DEPLOYMENT.md`, opravit tvrzení z 7.2, archivovat staré analýzy,
`BACKLOG.md` na otevřené položky.

**Metrika:** README a CLAUDE.md nelžou v žádném ověřitelném tvrzení.

### Fáze 7 – Refaktoring (průběžně) 🟡
`BaseScraper` ABC + parsing mixin (14 kroků, po každém `pytest`) · dokončit rozpad
`ListingDetail.razor` pod 800 ř. · rozhodnout o mrtvé `AnalysisJob` · HEIC/EXIF ·
integrační testy endpointů (`WebApplicationFactory` + Testcontainers) — největší mezera v testování.

---

## 9. Pořadí

```
Fáze 1 → Fáze 2 → Fáze 3 → Fáze 4 → Fáze 5 → Fáze 6 → Fáze 7
(½d)     (½d)     (½d)     (1-2d)   (1d)     (1d)     (průběžně)
```

První tři fáze zaberou dohromady půldruhého dne a pokrývají všechna tři reálná rizika:
někdo cizí na API, tichá smrt zdroje, ztráta dat. Zbytek je technický dluh bez časového tlaku.

## 10. Co tento dokument neřeší

- Kvalitu AI výstupů (SmartTags, PriceSignal, klasifikace fotek).
- Výkon na produkčních datech — `EXPLAIN ANALYZE` jsem nespouštěl.
- Hlubší audit zupagate infrastruktury (Traefik, certbot, DDNS, firewall) — prošel jsem jen to,
  co se dotýká RealEstate.
- Roadmapu funkcí (hybrid search, HNSW, scheduler) — zůstává v `BACKLOG.md`.

---

## 11. Dodatek – co bylo opraveno (25. 8. 2026)

Kódové opravy jsou v repu, testy zelené (155 C# / 106 pytest). Serverová část
je rozepsaná v `SUDGATE_CHECKLIST_2026-08-25.md`.

**Revize kapitoly 3 (LEXAMO):** scraper nebyl rozbitý. Parser funguje a kontejner
na `lexamo.cz` dosáhne — ověřeno z obou stran. Skutečná příčina: LEXAMO neplnil pole
`district`, takže geo filtr v `filters.py:_check_search_filters` skládal `combined_location`
jen z názvu obce („Vrbovec") a porovnával ho proti `target_districts: ["Znojmo", …]`.
Neshoda → zahodit. Padalo tak **všech 29** inzerátů, včetně ~12 z okresu Znojmo.
Oprava vytahuje okres ze slugu URL; ověřeno proti živému webu: **0 → 12 inzerátů**.

Zobecnění: `/v1/health/scrapers` hlásí `dead` i tam, kde je scraper v pořádku a jen
neprošel filtr. Signál je tedy „ze zdroje nic nepřibývá", ne „scraper je rozbitý" —
u alertu z Fáze 2 s tím počítat. Zdroje s podezřele nízkým počtem, kde může jít o totéž:
ZNOJMOREALITY (4), HVREALITY (7), DELUXREALITY (10).

**Nález navíc:** rate limiting nefungoval per IP. `AddFixedWindowLimiter(name, …)` vytváří
jeden sdílený limiter pro všechny volající, takže komentář „20 req/min per IP" nesouhlasil
s kódem a jeden klient mohl vyčerpat limit všem. Přepsáno na `AddPolicy` + partition podle IP,
plus `UseForwardedHeaders()` — za Traefikem by jinak všichni návštěvníci spadli do jednoho oddílu.

Zbývá otevřené: squash EF migrací (kap. 6.1), `BaseScraper` (6.3), rozpad
`ListingDetail.razor` (6.4), autentizace v aplikaci (kap. 2.4 květnové analýzy),
a ověření API klíče na sudgate (kap. 5.1) — to jediné jsem nemohl spustit.
