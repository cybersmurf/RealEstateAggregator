# Hloubková analýza projektu – 25. května 2026

Autor: GitHub Copilot · Stav: 1 558 inzerátů, 14 zdrojů, .NET 10 / Blazor / FastAPI / FastMCP

> **Cíl dokumentu:** Identifikovat všechno, co je v repozitáři rozbité, mrtvé nebo špinavé, navrhnout vyhození OneDrive exportu a komplexní redesign UI. Konec dokumentu obsahuje **implementační plán** rozdělený do 5 fází.

---

## 1. TL;DR – top 10 problémů

| # | Problém | Dopad | Obtížnost |
|---|---------|-------|-----------|
| 1 | Mrtvý projekt `RealEstate.Background` – v csproj, **bez jediného `.cs` souboru**, referencován z Api | Build noise, mate čtenáře | Triviální |
| 2 | `Listings.razor.orig` (37 kB) – residuum z merge konfliktu z 24. 2. 2026 | Mate IDE, FTS, gitové diffy | Triviální |
| 3 | OneDrive export – cca **350+ řádků služby + 178 řádků auth endpointů + 75 řádků UI v ListingDetail**, drží 2 sloupce v `listings`, sekreta, HttpClienty | Komplikuje UI i kód, user chce vyhodit | Středně |
| 4 | `ListingDetail.razor` = **2 825 řádků** v jednom souboru | Nelze rozumně udržovat, IDE umírá | Velká |
| 5 | `Listings.razor` = 1 140 řádků; logo map a SourceDto duplikované s `Home.razor` a `NavMenu.razor` | Drift, copy-paste bugy | Střední |
| 6 | `EnsureCreatedAsync()` místo `MigrateAsync()` – existující EF migrace v `Migrations/` jsou ignorované; schema se nahrazuje ručními SQL skripty v `scripts/migrate_*.sql` | Schema drift, migrace v gitu = lež | Střední |
| 7 | API endpointy nemají globální exception handler / ProblemDetails – `try/catch` v endpointech = **0** | Při chybě se vrací stack trace nebo prázdná 500 | Malá |
| 8 | Sekrety duplikované ve 2 lokacích (`secrets/` a `src/RealEstate.Api/secrets/`) – Colima bind mount na macOS nefunguje, kompenzuje se hackem v Makefile | Riziko mismatch, security | Malá |
| 9 | Žádný integrační test endpointu, žádný UI/Playwright test, žádné Python integrační testy DB upsertů | Regrese jdou tiše do prod | Velká |
| 10 | Žádná globální tematizace (dark mode, design tokens), spousta inline `style=""`, neúplná a11y (alt texty, focus, ARIA) | UX, WCAG 2.2 AA fail | Velká |

---

## 2. Rozbité a mrtvé části

### 2.1 Mrtvý kód k smazání
- `src/RealEstate.Background/` – csproj bez `.cs`. Reference v `RealEstateAggregator.slnx` a `RealEstate.Api.csproj`. **Smazat celé.** Skutečné hosted services už jsou v `RealEstate.Infrastructure/BackgroundServices/PlaywrightBootstrapHostedService.cs`.
- `src/RealEstate.App/Components/Pages/Listings.razor.orig` – residuum z mergu (Feb 24). **Smazat.**
- `src/RealEstate.Infrastructure/StorageServiceCollectionExtensions.cs:25-30` – `// TODO: Implement GoogleDriveStorageService` a `throw new NotImplementedException("OneDrive storage not yet implemented")`. Zbytek storage abstrakce (`IStorageService`) se reálně používá jen pro Local; multi-storage se nikdy nedokončilo. **Zjednodušit na `LocalStorageService` jako jediný registrovaný; smazat zbylé switch větve.**
- Nevyužitý `AnalysisService.CreateJobForListingAsync` – pohyboval se s plánem „async analysis jobs", ale produkce dnes používá `LocalAnalysisService` synchronně + MCP. `AnalysisJob` entita a tabulka jsou tedy mrtvé. **Buď reálně použít, nebo smazat entitu, DbInitializer kus a contracts.**
- `RealEstate.Api/secrets/` – kopie `secrets/`, Makefile to syncuje přes `docker cp` (viz Session 26). **Sjednotit na jediný zdroj `./secrets`** – buď bind mountem (Linux server) nebo `docker cp` (Colima). Druhý adresář smazat.

### 2.2 Migrace v rozpolceném stavu
- `src/RealEstate.Infrastructure/Migrations/` obsahuje 4 EF migrace (poslední `AddDispositionToListings` z 24. 2.).
- `Program.cs:121` ale volá `EnsureCreatedAsync()` – migrace se nikdy nepřehrávají, schema se opravuje:
  - `DbInitializer.cs` (ALTER TABLE IF NOT EXISTS …)
  - `scripts/migrate_*.sql` (PostGIS, cadastre, price_history, photo_classification, atd.)
- Důsledek: dev DB = svoboda hackovat, prod DB = ručně spouštět SQL z `make db`. **Migrace v gitu lžou** – nejsou pravdivým zdrojem schématu.
- Řešení v plánu níže (Fáze 4).

### 2.3 OneDrive – ke kompletnímu vyhození (rozhodnutí uživatele)
Sčítáno celkem **cca 1 100 řádků kódu + 2 DB sloupce + 1 secret soubor + 4 HttpClienty + 1 auth endpoint group**.

Soubory k **úplnému smazání**:
- `src/RealEstate.Api/Services/OneDriveExportService.cs` (341 ř.)
- `src/RealEstate.Api/Services/IOneDriveExportService.cs`
- `src/RealEstate.Api/Endpoints/OneDriveAuthEndpoints.cs` (178 ř.)
- `secrets/onedrive-token.json`

Soubory **k úpravě** (vyříznout OneDrive větve):
| Soubor | Co odebrat |
|---|---|
| `Program.cs:158` | `app.MapOneDriveAuthEndpoints();` |
| `ServiceCollectionExtensions.cs:56-72` | `IOneDriveExportService` DI + 3× `AddHttpClient("OneDrive…")` |
| `Endpoints/ExportEndpoints.cs` | endpoint `/export-onedrive`, `SaveAnalysis` (OneDrive-only), `isOneDrive` větve v `UploadInspectionPhotos` a `GetExportState` |
| `Services/ListingService.cs:167` | `HasOneDriveExport = entity.OneDriveFolderId is not null` |
| `Services/ListingExportContentBuilder.cs:9,14` | komentáře a `DirectUrl` doc |
| `Domain/Entities/Listing.cs:58-59` | `OneDriveFolderId`, `OneDriveInspectionFolderId` |
| `Domain/Entities/UserListingPhoto.cs:5,22` | doc komentáře |
| `Domain/Entities/AnalysisJob.cs:12` | komentář v `StorageProvider` (`// GoogleDrive, OneDrive, Local` → `// GoogleDrive, Local`) |
| `Infrastructure/RealEstateDbContext.cs:129-130` | property mapping |
| `Infrastructure/DbInitializer.cs:164-165` | `ALTER TABLE … ADD COLUMN onedrive_…` – nahradit `DROP COLUMN IF EXISTS` |
| `Infrastructure/Storage/IStorageService.cs:5` + `StorageServiceCollectionExtensions.cs:14,28-30` | OneDrive switch |
| `Contracts/Listings/ListingDetailDto.cs:42,45` | `HasOneDriveExport` |
| `Contracts/Analysis/AnalysisJobCreateDto.cs:5` + `AnalysisJobDto.cs:9` | default `"GoogleDrive"` zůstane, komentář upravit |
| `appsettings.json:53-54,70` + `appsettings.Development.json:18` | sekce `OneDriveExport` a `Storage.Providers.OneDrive` |
| `ListingDetail.razor` | **75 řádků** – tlačítko, sekce `_oneDriveResult`, `OneDriveExportedButNoSession`, upload větev, `SaveAnalysisToOneDriveAsync` |
| `tests/RealEstate.Tests/ExportBuilderTests.cs` | testy OneDrive-specifické chování |

DB migrace (nová): `scripts/migrate_drop_onedrive.sql`
```sql
ALTER TABLE re_realestate.listings DROP COLUMN IF EXISTS onedrive_folder_id;
ALTER TABLE re_realestate.listings DROP COLUMN IF EXISTS onedrive_inspection_folder_id;
```

### 2.4 Bezpečnost
- `appsettings.json` má cesty na secret soubory (paths, ne secrety) – OK, ale konfigurace by se měla převést na env proměnné (12-factor).
- `Program.cs:50` – fallback `apiKey = "dev-key-change-me"`. **V prod by mělo failnout startup**, pokud `API_KEY` není nastaven a `ASPNETCORE_ENVIRONMENT=Production`.
- CORS politika povoluje `http://localhost:5002` + `http://realestate-app:8080` s `AllowAnyHeader/AllowAnyMethod` – pro lokální dev OK, ale pro prod doménu (`realestate.sudata.eu`) tam **chybí origin**. Zkontrolovat, jestli prod Blazor App komunikuje s API přes IPC nebo zvenku.
- `EnsureCreatedAsync` se v Dev volá s **20 retry loopem**. Když ho někdo nedopatřením zapne v prod (`ASPNETCORE_ENVIRONMENT=Development`), přepíše schema. Ochranný check `!IsProduction` chybí.
- `Program.cs:101-130` – migrace běží v `if (app.Environment.IsDevelopment())`, ale Docker compose nastavuje `ASPNETCORE_ENVIRONMENT=Development` i v produkci na serveru → zkontrolovat.
- Žádný rate-limiter na `/api/listings/search` a `/api/rag/ask` (LLM volání = drahé). OWASP A04.
- Antiforgery vypnutý v `UploadInspectionPhotos` (`.DisableAntiforgery()`) – nutné kvůli multipart, ale endpoint by měl být chráněn alespoň `X-Api-Key`.

### 2.5 Backend technický dluh
- `LocalAnalysisService.cs` – **1 323 řádků**. Mix: stahování fotek, base64, 5 různých chat providerů (OpenRouter, Groq, Mistral, Anthropic, Ollama Cloud), DOCX export přes OpenXml, embedding. Vyžaduje rozpad: `IPhotoDescriber`, `IChatCompletionProvider` (s implementacemi per-vendor), `AnalysisDocxRenderer`.
- `PhotoClassificationService.cs` – 753 řádků; podobný problém, ale jen Ollama Vision + Mistral – rozumněji členitelný.
- `CadastreService.cs` – 602 řádků; HTTP + parsing + OCR + DB. OCR část (volání Ollama Vision) by mohla sdílet helper s `PhotoClassificationService`.
- `SpatialService.cs` – 621 řádků; OSRM + Nominatim + PostGIS raw SQL. Lze rozdělit na `IGeocodingService` + `IRoutingService` + repo.
- `ListingService.cs` – 720 řádků – ne nejhorší, ale obsahuje 6 různých metod (Search, Detail, MyListings, ExportCsv, UpdateState, DeactivateDead). CSV export se odděluje do `ListingCsvExporter`.
- **Žádný HttpClient typed wrapper** – všude `IHttpClientFactory.CreateClient("Name")`. Lehčí refactor: typed clienti (`OllamaHttpClient`, `NominatimHttpClient`) s konfigurací v DI.
- Endpoints obsahují **nulový try/catch**, žádný `app.UseExceptionHandler(...)`. Při výjimce odejde 500 bez Problem+JSON tělo. **Přidat `AddProblemDetails()` + `UseExceptionHandler()`.**
- `ListingEndpoints.cs` GET `/api/listings/{id}/price-history` používá `SqlQueryRaw<PriceHistoryRow>` – OK, ale entita `ListingPriceHistory` už existuje – přejít na typed query.

### 2.6 Backend testy
Aktuálně 79 C# testů ve 4 souborech (`Cadastre`, `ExportBuilder`, `Rag`, `UnitTest1`). Zcela chybí:
- Endpoint tests (`WebApplicationFactory<Program>`) pro `ListingEndpoints`, `RagEndpoints`, `ScrapingEndpoints` (auth).
- Service tests pro `ListingService` (filter predicates, sort tie-breaker).
- `SpatialService` – ST_Buffer SQL test (testcontainers PostGIS).
- `OllamaTextService` – JSON robust parsing edge cases (známý problematický bod podle docs).
- Snapshot test pro `ListingExportContentBuilder` – už existuje, rozšířit po OneDrive removalu.

### 2.7 UI/UX problémy

**Strukturální**:
- `ListingDetail.razor` – 2 825 řádků. Drží: header, carousel, klasifikaci fotek, detail tabulku, popis, AI insights, historii cen, KN (OCR + RUIAN), state, export sekci (Drive + **OneDrive**), zápis prohlídky. Musí se rozpadnout na komponenty:
  - `ListingHeaderCard.razor`
  - `ListingPhotoCarousel.razor`
  - `ListingPhotoClassificationGrid.razor` (s lightboxem)
  - `ListingDetailsTable.razor`
  - `ListingAiInsightsPanel.razor`
  - `ListingPriceHistory.razor`
  - `ListingCadastrePanel.razor` (KN OCR + RUIAN)
  - `ListingUserStateBar.razor`
  - `ListingExportPanel.razor` (jen Google Drive po cleanupu)
  - `ListingInspectionPhotos.razor`

- `Listings.razor` – 1 140 řádků, filter + grid + table view + quick filters + chip-sorting. Rozdělit na:
  - `Listings.razor` (page shell + state + URL params)
  - `ListingsFilterPanel.razor`
  - `ListingsQuickFilters.razor`
  - `ListingsResultsToolbar.razor` (count, CSV, view toggle, sort chips)
  - `ListingCard.razor` (už máme instrukce v `.github/instructions/listing-card-badges.instructions.md`)
  - `ListingsTable.razor`
  - `ListingsPagination.razor`

- `Map.razor` – 700 řádků; rozdělit na `MapCorridorPanel`, `MapGpsCoveragePanel`, `MapPhotoDownloadPanel`.

- Žádná složka `Components/Shared/` (nebo podobně) – komponenty žijí jen jako page-level `.razor` soubory. **Vytvořit `Components/Listings/`, `Components/Common/`, `Components/Layout/`.**

**Duplicity**:
- Logo map (`_logoMap`) duplikovaná v `Home.razor:172-189` i `Listings.razor` (a podle všeho i `MyListings.razor`, `Map.razor`). **Vytáhnout do `Services/SourceLogoProvider.cs` nebo statického `Constants/SourceLogos.cs`.**
- `SourceDto` / `SourceNavDto` definováno 3× (Home, NavMenu, Listings). Sjednotit do `Models/SourceDto.cs` (už existuje – jen ho používat).
- Pattern „spinner + label v MudButton" je copy-paste 20+ krát v `ListingDetail.razor`. Helper komponenta `<LoadingButton Text=… Loading=… OnClick=…>`.
- KN tabulka „Co zkontrolovat v KN" vs „Inzerát vs Katastr" se překrývají, dva typy zobrazení – pojednat jako jednu komponentu s `ViewMode`.

**Konzistence**:
- Tlačítka mají náhodně `Variant.Filled` × `Variant.Outlined` × `Variant.Text` – chybí design systém.
- Barvy: `Color.Primary/Secondary/Success/Info/Warning/Error` se používají sémanticky nejednotně (Info = OneDrive, Success = Drive – po vyhození OD bude Drive = Primary; KN = Info, ale Info = OD; AI = Secondary).
- `Snackbar.Add(...)` vs `MudAlert` vs `MudText Color="Color.Error"` – tři způsoby chybového feedbacku.
- Inline `style="..."` – cca 60+ výskytů jen v ListingDetail. Migrovat do `ListingDetail.razor.css` (component-scoped).
- `Console.WriteLine` v `Home.razor:160` – v Blazor Server jde do server logu, ale ne strukturovaně. Použít `ILogger<Home>`.

**Accessibility (WCAG 2.2 AA gap analysis)**:
- `<img>` v carouselu má `alt="Foto"` – nedostatečné (mělo by být `alt="Fotografie nemovitosti @photoIndex z @total"`).
- Drop-zóna pro KN OCR má `aria-label`, ale chybí `role="button"` + `tabindex="0"` + Enter handler.
- `MudIconButton` v klasifikačních feedback ikonkách – chybí často `aria-label` (ano, máme ho, ale ne všude).
- Heading hierarchy v ListingDetail: skáče h4 → h6 → h6 (chybí h5).
- Žádný „skip to main content" link.
- Color-only signál v cenovém trendu (zelená/červená) – přidat ikonu (už máme `MudChip Icon`, ale ne všude).
- Žádný viditelný focus ring na Mud komponentách – testovat klávesnicí.
- Žádný `prefers-reduced-motion` handling u Carouselu.

**Mobile**:
- `MudExpansionPanel` filtrový panel je defaultně `Expanded=true` – na mobilu zabírá celou obrazovku. Default `Expanded=false` + chip „🔍 Filtry aktivní (3)" je lepší UX.
- Detail page má hodně tlačítek vedle sebe – `MudStack Row=true` se na mobilu zalamuje, ale ne hezky.
- Carousel `Style="height:400px"` – fixní výška na mobile = ořezané fotky.

**Funkční mezery**:
- Nikde není seen / unseen indikátor (uživatel neví, kterou kartu už klikl).
- Není možnost porovnat 2-3 inzeráty side-by-side (důležitý use-case pro nákupní rozhodnutí).
- Filtry se neukládají do URL query (jen do ProtectedSessionStorage) – nelze sdílet odkaz.
- Není „uložené hledání" / „upozornit na nové" – přitom DB má `user_listing_states`.
- Notifikace o nových inzerátech (po nočním scrapu) – zatím jen Slack pro errory, ne pro novinky.
- Carousel nemá fullscreen (lightbox je jen pro AI klasifikaci fotek – přidat i pro hlavní carousel).
- Decision report (`/decision-report`) je 522 řádků a podle commit historie experimentální – ověřit, zda se používá nebo zda smazat.

---

## 3. Vlastnosti, které by se mohly přidat

| Idea | Hodnota | Náklad |
|---|---|---|
| **Lightbox pro hlavní carousel** (existuje pro AI grid) | Vysoká – fotky jsou srdce inzerátu | Nízký |
| **Porovnání inzerátů side-by-side** (`/compare?ids=a,b,c`) | Vysoká – rozhodovací nástroj | Střední |
| **URL state pro filtry** (`?q=znojmo&type=house&priceMax=5000000`) | Střední – sdílení odkazů | Nízký |
| **Saved searches + e-mail/Slack alert** na nové inzeráty | Vysoká – aktuálně musíš ručně otvírat | Střední |
| **„Seen" indikátor na kartách** (klikl jsem, mám přečteno) | Střední | Nízký |
| **Cluster markery na mapě** (1 500+ bodů zatím vykresluje všechno) | Střední – performance | Nízký (Leaflet.markercluster) |
| **Dark mode + design tokens (CSS proměnné)** | Vysoká – UX kvality | Střední |
| **Heatmapa cen v mapě (ST_AsGeoJSON + Turf grid)** | Vysoká – unikátní feature | Vysoký |
| **Drobné info-statistiky na home (medián ceny, nejlevnější obec…)** | Střední | Nízký |
| **PWA manifest + offline shell** | Nízká–Střední | Střední |
| **GraphQL/OData filter na `/api/listings`** | Nízká – už máme REST | Střední |
| **Mobile bottom nav místo MudDrawer** | Vysoká pro mobil | Střední |

---

## 4. Implementační plán

Plán je v **5 fázích**. Každá fáze končí zelenými testy a deployem do staging/prod. Doporučené pořadí respektuje rizika (nejdřív cleanup, pak UI).

### 🟦 Fáze 1 – Čištění (1 PR, ~1 den)
**Cíl:** Smazat mrtvý kód, sjednotit secrets, opravit obvious chyby.

1. `git rm src/RealEstate.App/Components/Pages/Listings.razor.orig`
2. **Smazat projekt `RealEstate.Background`**:
   - `git rm -r src/RealEstate.Background/`
   - Z `RealEstateAggregator.slnx` odstranit `<Project Path="...">`
   - Z `RealEstate.Api.csproj` odstranit `<ProjectReference>`
3. Sjednotit secrets: vyřadit `src/RealEstate.Api/secrets/`, `Makefile secrets-sync` zachovat, ale jen jako `docker cp` ze `./secrets`. Update `.gitignore` + `docs/CLOUD_STORAGE.md`.
4. `EnsureCreatedAsync` hardening: obalit guardem `if (app.Environment.IsDevelopment() && Env.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Production")` – kompletní fix až ve Fázi 4.
5. Globální exception handler:
   ```csharp
   builder.Services.AddProblemDetails();
   app.UseExceptionHandler();
   app.UseStatusCodePages();
   ```
6. API key startup check: `if (builder.Environment.IsProduction() && apiKey == "dev-key-change-me") throw new InvalidOperationException("API_KEY must be set in production");`
7. Doplnit testy pro nový exception handler (1 endpoint test).

**Akceptační kritéria:** `dotnet build` zelený, `dotnet test` 79+ zelený, kontejnery startují.

---

### 🟥 Fáze 2 – Odstranění OneDrive exportu (1 PR, ~1 den)
**Cíl:** Kompletně odstranit OneDrive funkcionalitu.

1. Smazat `OneDriveExportService.cs`, `IOneDriveExportService.cs`, `OneDriveAuthEndpoints.cs`.
2. Z `Program.cs` odstranit `MapOneDriveAuthEndpoints()`.
3. Z `ServiceCollectionExtensions.cs` odstranit `IOneDriveExportService` DI + 3 HttpClient.
4. V `ExportEndpoints.cs` odstranit `/export-onedrive`, `SaveAnalysis` (přejmenovat zbytek), `isOneDrive` větve.
5. V `ListingDetail.razor` smazat 75 OneDrive řádků + odpovídající C# kód (`_oneDriveResult`, `_oneDriveExporting`, `ExportToOneDriveAsync`, `CopyOneDriveUrlAsync`, `SaveAnalysisToOneDriveAsync`, `UploadOneDriveInspectionPhotosAsync`, `_oneDriveInspectionFiles`, `OneDriveInspectionId`, `OneDriveExportedButNoSession`).
6. V `ListingService.cs` smazat `HasOneDriveExport` + DTO field.
7. Domain: smazat `Listing.OneDriveFolderId`, `Listing.OneDriveInspectionFolderId`.
8. EF: smazat property mapping v `RealEstateDbContext.cs`.
9. DbInitializer: nahradit `ADD COLUMN onedrive_*` za `DROP COLUMN IF EXISTS onedrive_*`.
10. Nová migrace `scripts/migrate_drop_onedrive.sql` – pro produkci (dev používá `EnsureCreatedAsync` → DROP COLUMN si vezme z DbInitializer při příštím startu).
11. `appsettings.json`/`appsettings.Development.json` – odstranit `OneDriveExport` a `Storage.Providers.OneDrive` sekce.
12. `secrets/onedrive-token.json` – `git rm`.
13. Aktualizovat docs (`CLOUD_STORAGE.md`, `README.md`, `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`) – vyhodit OneDrive zmínky.
14. Aktualizovat / upravit `ExportBuilderTests.cs` (testy OneDrive-only chování smazat).

**Akceptační kritéria:** `grep -rn -i onedrive src/ tests/ docs/ scripts/ docker-compose.yml | wc -l` = 0. Existující GD export funguje beze změny.

---

### 🟩 Fáze 3 – UI/UX refactor (3 PR, ~3-5 dnů)
**Cíl:** Rozumný design systém a komponentová architektura.

**PR 3a – Design systém + společné komponenty:**
1. `src/RealEstate.App/Components/Common/` adresář:
   - `LoadingButton.razor` – sjednocení spinner+text+icon patternu (eliminuje 20+ míst)
   - `ConfirmDialog.razor` – pro destruktivní akce
   - `EmptyState.razor` – `Icon`, `Title`, `Description`, `Action`
   - `SectionCard.razor` – wrapper `MudPaper` s heading + slot
   - `SourceLogoBadge.razor` – `<Code/>` → logo `<img>` nebo fallback
2. `Services/SourceLogoProvider.cs` – jediný zdroj `_logoMap`. Eliminuje 3 duplicity.
3. CSS proměnné v `wwwroot/css/app.css`:
   ```css
   :root {
     --re-radius: 12px;
     --re-shadow: 0 2px 8px rgba(0,0,0,.06);
     --re-spacing-card: 16px;
     --re-color-price-low: #2e7d32;
     --re-color-price-high: #d32f2f;
   }
   @media (prefers-color-scheme: dark) { ... }
   ```
4. `MudThemeProvider` v `MainLayout.razor` – aktivovat tmavý režim s toggle v NavMenu.
5. Migrovat inline `style="..."` z `ListingDetail.razor` do `ListingDetail.razor.css`.

**PR 3b – Rozpad ListingDetail:**
1. Vytvořit `Components/Listings/` adresář.
2. Postupně extrahovat 10 komponent z `ListingDetail.razor` (viz §2.7).
3. Cílový stav: `ListingDetail.razor` < 400 řádků (jen orchestrátor + state).
4. Carousel: přidat `Lightbox` (lze re-use stávající z `_lbOpen` patternu) + fullscreen + klávesnicí ovládání.
5. Mobile: filtry default collapsed s chipem aktivních filtrů, carousel `height: clamp(220px, 50vw, 400px)`.

**PR 3c – Rozpad Listings + Map + Quality of Life:**
1. Rozpad `Listings.razor` na 6 komponent (viz §2.7).
2. URL query param state (`?q=&type=&priceMax=&page=`) místo (nebo navíc k) `ProtectedSessionStorage`.
3. „Seen" indikátor: po kliknutí na kartu se uloží do `localStorage` (volitelně do `user_listing_states`), karta dostane bledší pozadí + ikonu.
4. Rozpad `Map.razor` na 3 panely.
5. Cluster markery na mapě (Leaflet.markercluster, JS interop).
6. Skip-to-content link v `MainLayout.razor`.
7. Heading hierarchy fix v ListingDetail.

**Akceptační kritéria:** Žádný `.razor` soubor > 600 řádků. `axe DevTools` na 3 hlavních stránkách = 0 critical issues. Playwright smoke test (přidat).

---

### 🟧 Fáze 4 – Migrace + Backend kvalita (1-2 PR, ~2 dny)
**Cíl:** Pravdivé migrace, lepší testy, rozpad obřích služeb.

1. **EF migrace zase pravda:**
   - Reset migration history: `git rm src/RealEstate.Infrastructure/Migrations/*` (4 staré soubory).
   - Nasadit do prod ručně přes SQL skripty zachovat (`scripts/migrate_*.sql`), ale paralelně vytvořit `dotnet ef migrations add InitialFromCurrentSchema` z `EnsureCreated` výsledku.
   - Přepnout `Program.cs`: v prod `MigrateAsync()`, v dev `EnsureCreatedAsync()` zachovat za env flagem.
   - Aktualizovat `docs/DEPLOYMENT.md`.

2. **Rozpad `LocalAnalysisService` (1 323 ř.):**
   - `IChatCompletionProvider` + impl: `OpenRouterChatProvider`, `GroqChatProvider`, `MistralChatProvider`, `AnthropicChatProvider`, `OllamaCloudChatProvider`, `OllamaLocalChatProvider`.
   - `IPhotoDescriber` (Ollama Vision wrapper – sdílený s `PhotoClassificationService` a `CadastreService.OcrScreenshot`).
   - `AnalysisDocxRenderer` (DOCX builder).
   - `LocalAnalysisService` → orkestrátor (~200 ř.).

3. **Typed HttpClients** místo `IHttpClientFactory.CreateClient("Name")`:
   - `OllamaHttpClient`, `NominatimHttpClient`, `OsrmHttpClient`, `RuianHttpClient`, `MistralHttpClient`.

4. **Endpoint integration tests** – `WebApplicationFactory<Program>` + testcontainers PostgreSQL (postgis/postgis:15-3.4). Pokrytí:
   - `POST /api/listings/search` – filtr predicates.
   - `GET /api/listings/{id}` – existující + non-existing.
   - `POST /api/scraping/trigger` bez X-Api-Key → 401.
   - `GET /api/listings/{id}/price-history`.

5. **Rate limiter:**
   ```csharp
   builder.Services.AddRateLimiter(opts => {
     opts.AddFixedWindowLimiter("rag", o => { o.PermitLimit = 10; o.Window = TimeSpan.FromMinutes(1); });
   });
   app.MapPost("/api/rag/ask", ...).RequireRateLimiting("rag");
   ```

**Akceptační kritéria:** `dotnet ef migrations script` generuje validní SQL = matchuje DbInitializer. Endpoint testy > 15 pass. Žádný service > 600 ř.

---

### 🟪 Fáze 5 – Nové vlastnosti (postupně, dle priorit)
1. **Saved searches + Slack alert na nové inzeráty** – Postgres `saved_searches` tabulka, `BackgroundWorker` v API spuštěný po denním scrapu, posílá Slack notifikaci.
2. **Porovnání 2-3 inzerátů** (`/compare?ids=a,b,c`) – nová stránka, side-by-side tabulka klíčových polí + fotky thumbnail.
3. **Cluster markery + heatmapa cen na mapě** (Leaflet.markercluster + Leaflet.heat).
4. **PWA shell** (`manifest.webmanifest`, service worker pro offline /listings cache).
5. **Mobile bottom navigation** – přepnout MudDrawer pro mobilní viewport.

---

## 5. Doporučené pořadí a metrika úspěchu

| Fáze | Hlavní hodnota | Risk | Reverzibilita |
|---|---|---|---|
| 1. Čištění | Snížení noise, oprava security gaps | Nízký | Vysoká |
| 2. OneDrive removal | Zjednodušení, naplnění uživatelského záměru | Nízký | Středně (DB sloupce smazané) |
| 3. UI refactor | Drastické zlepšení UX a údržby | Střední (visual regressions) | Střední |
| 4. Migrace + backend | Stabilita produkce | Vysoký (DB) | Nízká |
| 5. Nové vlastnosti | Konkurenční výhody | Nízký | Vysoká |

**Metriky úspěchu po dokončení Fáze 1-4:**
- 0× `OneDrive` v src/
- Žádný `.razor` ani `.cs` soubor > 700 řádků
- `dotnet test` ≥ 120 zelených (současných 79 + 40 nových endpoint testů)
- Lighthouse score na 3 hlavních stránkách: Performance ≥ 85, Accessibility ≥ 95
- `axe-core` critical issues = 0
- Skutečné EF migrace = pravda (žádný `EnsureCreatedAsync` v prod)
- ProblemDetails místo prázdných 500
- Rate limit na RAG endpoint

---

## 6. Co tento dokument záměrně neřeší

- **Python scraper kvalita** – mimo rozsah požadavku; podle commit historie poslední týdny stabilizace, score je rozumné.
- **MCP server** – zatím funkční, nevyžaduje refactor.
- **DecisionReport.razor** (522 ř.) – nezahrnuto, dokud uživatel nepotvrdí, jestli ho aktivně používá.
- **Konkrétní designové mockupy** – tato analýza dodává *architekturu*; vizuální mockupy by si zasloužily zvláštní iteraci s uživatelem.
- **Migrace na Blazor WebAssembly nebo .NET Aspire** – ne, current Blazor Server stack je v pořádku pro tento projekt.

---

*Konec dokumentu. Připraveno k diskuzi a postupné implementaci po fázích.*
