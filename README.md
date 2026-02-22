# Real Estate Aggregator

> **Komplexní agregátor realitních inzerátů s pokročilým filtrováním a AI analýzou**  
> *.NET 9 • MudBlazor 9 • Python Scraping • PostgreSQL*

---

## 📋 Přehled projektu

Real Estate Aggregator je systém pro automatický sběr, normalizaci a správu realitních inzerátů z různých zdrojů (realitních kanceláří). Umožňuje centralizované vyhledávání, filtrování, označování a analýzu nemovitostí bez nutnosti procházet jednotlivé weby realitek.

### Klíčové funkce

✅ **Automatický scraping** – pravidelný sběr inzerátů z vybraných RK  
✅ **Jednotný datový model** – normalizace různorodých formátů  
✅ **Pokročilé filtrování** – lokalita, cena, plocha, typ, stav  
✅ **User management** – označování (líbí/nelíbí), poznámky, favority  
✅ **AI analýza** – export inzerátu + fotek do cloudu pro zpracování AI  
✅ **Moderní UI** – Blazor + MudBlazor s responsivním designem  

---

## 🏗️ Architektura

```
┌─────────────────────────────────────────────────────────────┐
│                    Frontend (Blazor + MudBlazor)             │
│  • Listingy s filtry  • Detail inzerátu  • AI analýzy       │
└──────────────────────┬──────────────────────────────────────┘
                       │ REST API
┌──────────────────────▼──────────────────────────────────────┐
│              Backend (.NET 9 - ASP.NET Core)                 │
│  • Business logika  • EF Core  • Background služby          │
└──────────────────────┬──────────────────────────────────────┘
                       │
          ┌────────────┴────────────┐
          │                         │
┌─────────▼─────────┐    ┌─────────▼──────────┐
│  PostgreSQL DB     │    │  Cloud Storage     │
│  • Inzeráty        │    │  • Google Drive    │
│  • Fotky           │    │  • OneDrive        │
│  • User stavy      │    │  • Analytické docs │
└────────────────────┘    └────────────────────┘
          ▲
          │ DB write
┌─────────┴──────────────────────────────────────────────────┐
│              Scraping služba (Python)                       │
│  • Remax  • MM Reality  • Prodejme.to  • další zdroje      │
└────────────────────────────────────────────────────────────┘
```

---

## 🛠️ Technologický stack

### Backend (.NET 9)
- **Framework**: ASP.NET Core 9.0
- **UI**: Blazor Web App + MudBlazor 9.x
- **ORM**: Entity Framework Core 9
- **Databáze**: PostgreSQL (primární) / MSSQL
- **API integrace**: 
  - Google Drive API (.NET Client)
  - Microsoft Graph API (OneDrive)

### Scraping (Python)
- **Jazyk**: Python 3.12+
- **HTTP**: `httpx` / `requests`
- **Parsing**: `BeautifulSoup4` / `parsel`
- **Headless browser**: `Playwright` (pro JS-heavy weby)
- **DB**: `asyncpg` / `psycopg` / `SQLAlchemy`
- **Scheduler**: `APScheduler` / cron

### Infrastruktura
- **Hosting**: Docker / Azure / AWS / on-premise
- **Storage**: Google Drive / OneDrive / Azure Blob
- **CI/CD**: GitHub Actions

---

## 📁 Struktura projektu

```
RealEstateAggregator/
├── src/
│   ├── RealEstate.Api/              # ASP.NET Core Web API
│   ├── RealEstate.App/              # Blazor frontend (MudBlazor)
│   ├── RealEstate.Domain/           # Doménové modely, enums, rozhraní
│   ├── RealEstate.Infrastructure/   # EF Core, repositories, cloud integrace
│   └── RealEstate.Background/       # Background služby (AnalysisJob)
│
├── tests/
│   └── RealEstate.Tests/            # Unit + integration testy
│
├── scraper/                         # Python scraping projekt
│   ├── scrapers/                    # Implementace scraperů pro jednotlivé RK
│   │   ├── remax_scraper.py
│   │   ├── mmreality_scraper.py
│   │   └── prodejme_to_scraper.py
│   ├── core/                        # Společná logika
│   │   ├── models.py
│   │   ├── db.py
│   │   └── runner.py
│   └── config/
│       └── settings.yaml
│
├── docs/                            # Dokumentace
│   ├── BACKLOG.md                   # Product backlog
│   ├── TECHNICAL_DESIGN.md          # Technický návrh
│   ├── API_CONTRACTS.md             # API dokumentace
│   └── DEPLOYMENT.md                # Deployment guide
│
├── RealEstateAggregator.sln         # .NET solution
└── README.md                        # Tento soubor
```

---

## 🚀 Rychlý start

### Požadavky
- .NET 9 SDK
- Python 3.12+
- PostgreSQL 15+
- Node.js 20+ (pro Blazor dev tools)

### 1. Databáze
```bash
# Spustit PostgreSQL
docker run --name realestate-db -e POSTGRES_PASSWORD=dev -p 5432:5432 -d postgres:15

# Vytvořit databázi
psql -h localhost -U postgres -c "CREATE DATABASE realestate_dev;"
```

### 2. Backend (.NET)
```bash
cd src/RealEstate.Api
dotnet restore
dotnet ef database update
dotnet run
```
Backend běží na `https://localhost:5001`

### 3. Scraper (Python)
```bash
cd scraper
python -m venv venv
source venv/bin/activate  # Windows: venv\Scripts\activate
pip install -r requirements.txt
python -m core.runner
```

### 4. Frontend
Frontend je součástí Blazor Web App, dostupný na `https://localhost:5001`

---

## 📊 Datový model (jádro)

### Source
Zdroj inzerátů (realitní kancelář)
- `Id`, `Name`, `BaseUrl`, `IsActive`

### Listing
Normalizovaný inzerát
- Základní info: `Title`, `Description`, `Url`, `ExternalId`
- Kategorizace: `PropertyType`, `OfferType`
- Cena: `Price`, `PriceNote`
- Lokace: `LocationText`, `Region`, `District`, `Municipality`
- Parametry: `AreaBuiltUp`, `AreaLand`, `Rooms`, `ConstructionType`, `Condition`
- Metadata: `FirstSeenAt`, `LastSeenAt`, `IsActive`

### ListingPhoto
Fotografie inzerátu
- `ListingId`, `OriginalUrl`, `StoredUrl`, `Order`

### UserListingState
Stav inzerátu per uživatel
- `UserId`, `ListingId`, `Status` (New/Liked/Disliked/Ignored/ToVisit/Visited)
- `Notes`, `LastUpdated`

### AnalysisJob
AI analýza inzerátu
- `ListingId`, `Status`, `StorageProvider`, `StoragePath`
- `RequestedAt`, `FinishedAt`, `ErrorMessage`

---

## 🎯 API Endpoints (přehled)

### Listings
- `GET /api/listings` – seznam s filtrací a paginací
- `GET /api/listings/{id}` – detail inzerátu
- `POST /api/listings/{id}/state` – uložit user stav

### Sources
- `GET /api/sources` – seznam realitních kanceláří

### Analysis
- `POST /api/listings/{id}/analysis` – spustit AI analýzu
- `GET /api/analysis/{jobId}` – stav analýzy

---

## 🔄 Workflow scrapingu

1. **Periodický job** (cron/timer) spustí runner
2. **Runner** projde všechny aktivní scrapers
3. Pro každý scraper:
   - Fetch listings (paginace přes listing stránky)
   - Fetch detail (HTML/JSON detailu)
   - Normalize (parsování → strukturovaná data)
4. **Upsert do DB**:
   - Nový inzerát → insert + `FirstSeenAt`
   - Existující → update ceny/parametrů + `LastSeenAt`
5. **Deaktivace** – inzeráty neviděné X běhů → `IsActive = false`

---

## 🧠 Funkce "Udělej analýzu"

Vytvoří balíček pro AI zpracování:

1. Uživatel klikne "Udělej analýzu" na inzerátu
2. Backend vytvoří `AnalysisJob` (status: Pending)
3. Background service:
   - Stáhne listing data + fotky
   - Vygeneruje dokument (Markdown/HTML/Word):
     - Tabulka parametrů
     - Originální text
     - Seznam fotek
   - Nahraje na Google Drive / OneDrive
4. `AnalysisJob.Status` → Succeeded, uložen link
5. Frontend zobrazí tlačítko "Otevřít v Drive"

---

## 📈 Roadmap

### MVP (v1.0)
- [x] Základní scraping (3 zdroje: Remax, MM Reality, Prodejme.to)
- [x] .NET backend s EF Core
- [x] Blazor frontend s MudBlazor
- [x] Filtrování a user stavy
- [x] AI analýza s Google Drive exportem

### v1.1
- [ ] Autentizace/autorizace (ASP.NET Identity)
- [ ] Push notifikace o nových inzerátech
- [ ] Export do PDF
- [ ] Pokročilý fulltext search

### v1.2
- [ ] Mapové zobrazení inzerátů
- [ ] Porovnání inzerátů vedle sebe
- [ ] Integrace s AI pro automatické hodnocení
- [ ] Mobile app (MAUI)

---

## 📝 Licence

Tento projekt je privátní. Všechna práva vyhrazena.

---

## 🤝 Kontakt

Pro otázky a podporu kontaktujte vlastníka projektu.

**Vytvořeno**: Únor 2026  
**Verze**: 1.0.0-alpha
