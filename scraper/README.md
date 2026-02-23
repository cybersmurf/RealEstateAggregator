# RealEstate Scraper - Python FastAPI

Python FastAPI aplikace pro scraping realitních inzerátů z českých realitních portálů.

## 🚀 Quick Start

### 1. Instalace dependencies

```bash
cd scraper

# Vytvořit virtual environment
python -m venv venv
source venv/bin/activate  # Mac/Linux
# venv\Scripts\activate  # Windows

# Instalovat balíčky
pip install -r requirements.txt

# Pokud používáš Playwright (pro JS-heavy weby)
playwright install
```

### 2. Spuštění FastAPI serveru

```bash
# Spustit API server na portu 8001
python run_api.py

# Nebo pomocí uvicorn přímo
uvicorn api.main:app --host 0.0.0.0 --port 8001 --reload
```

Server poběží na: **http://localhost:8001**

### 3. Swagger dokumentace

Otevři prohlížeč: **http://localhost:8001/docs**

## 📡 API Endpoints

### `POST /v1/scrape/run`

Spustí scraping job v pozadí.

**Request body:**
```json
{
  "source_codes": ["REMAX", "MMR", "PRODEJMETO", "ZNOJMOREALITY", "SREALITY", "NEMZNOJMO", "HVREALITY"],
  "full_rescan": false
}
```

**Response:**
```json
{
  "job_id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "Queued",
  "message": "Scraping job enqueued."
}
```

### `GET /v1/scrape/jobs/{job_id}`

Získá status konkrétního jobu.

**Response:**
```json
{
  "job_id": "550e8400-e29b-41d4-a716-446655440000",
  "source_codes": ["REMAX"],
  "full_rescan": false,
  "created_at": "2026-02-22T10:30:00",
  "status": "Succeeded",
  "error_message": null
}
```

### `GET /v1/scrape/jobs`

Vrátí seznam všech jobů.

## 🔧 Architektura

```
scraper/
├── api/
│   ├── main.py          # FastAPI aplikace, endpointy
│   └── schemas.py       # Pydantic modely (DTOs)
├── core/
│   ├── runner.py        # Job orchestrator
│   └── scrapers/
│       ├── remax_scraper.py
│       ├── mmreality_scraper.py
│       ├── prodejmeto_scraper.py
│       ├── sreality_scraper.py
│       └── znojmoreality_scraper.py
├── config/
│   └── settings.yaml    # Konfigurace
├── requirements.txt
└── run_api.py          # Startup script
```

## 🔌 Integrace s .NET API

.NET API volá Python API pomocí HttpClient:

```csharp
// .NET endpoint
POST /api/scraping/trigger
  ↓
HttpClient POST http://localhost:8001/v1/scrape/run
  ↓
Python FastAPI spustí background job
  ↓
Job runner zavolá scrapers (Remax, MMR, Prodejme.to, Znojmo Reality, Sreality)
```

## 📝 Implementace scraperů

Každý scraper implementuje `run()` metodu:

```python
class RemaxScraper:
    async def run(self, full_rescan: bool = False) -> int:
        # 1. Fetch list stránky
        # 2. Parse inzeráty
        # 3. Fetch detail stránek
        # 4. Normalizuj data
        # 5. Ulož do DB
        return scraped_count
```

### TODO pro production ready scrapers:

- [ ] Stránkování (iterovat přes všechny stránky, kde to dává smysl)
- [ ] Error handling a retry logika
- [ ] Rate limiting (respektovat servery)
- [ ] Logging do structured logs
- [ ] Detekce změn (scrapovat jen nové/updatnuté)
- [ ] Photo download a storage
- [ ] Proxy support (pokud je potřeba)

## 🧪 Testování

```bash
# Spustit API
python run_api.py

# V jiném terminálu - test endpointu
curl -X POST http://localhost:8001/v1/scrape/run \
  -H "Content-Type: application/json" \
  -d '{"source_codes": ["REMAX"], "full_rescan": false}'

# Sledovat status jobu
curl http://localhost:8001/v1/scrape/jobs/{job_id}
```

## 🐳 Docker

V `docker-compose.yml` už máš připravený service:

```yaml
scraper:
  build: ./scraper
  ports:
    - "8001:8001"
  environment:
    - DATABASE_URL=postgresql://postgres:dev@postgres:5432/realestate_dev
```

## 📚 Další kroky

1. **DB integrace**: Napoj scrapers na PostgreSQL pomocí asyncpg
2. **Scheduling**: APScheduler pro automatické spouštění (např. každých 12 hodin)
3. **Monitoring**: Logování do structured logs, metriky
4. **Proxy pooling**: Pokud weby blokují, přidat proxy rotaci
5. **Redis queue**: Místo in-memory dictionary použít Redis pro joby

---

**Happy scraping!** 🏠✨
