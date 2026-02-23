# ============================================================
# RealEstateAggregator – Dev Makefile
# Hybridní dev mod: Postgres + Scraper v Dockeru, .NET lokálně
# ============================================================

.PHONY: help up down rebuild scraper api app all logs status test-scrape seed

VENV = scraper/.venv/bin/python
SCRAPER_DIR = scraper

help:
	@echo "================================================="
	@echo "  RealEstateAggregator – Dev Commands"
	@echo "================================================="
	@echo "  make up           – Spustí postgres + scraper v Dockeru"
	@echo "  make rebuild      – Přebuildi scraper image a restartuj"
	@echo "  make down         – Zastaví všechny Docker kontejnery"
	@echo "  make api          – Spustí .NET API lokálně (port 5001)"
	@echo "  make app          – Spustí Blazor App lokálně (port 5002)"
	@echo "  make all          – up + api + app (vše)"
	@echo "  make logs         – Logy scraper kontejneru"
	@echo "  make status       – Stav všech služeb"
	@echo "  make test-scrape  – Spustí testovací scrape (REMAX, CENTURY21)"
	@echo "  make seed         – Seed sources přes DB SQL"
	@echo "================================================="

# ---- Docker (postgres + scraper) ----

up:
	@echo ">>> Spouštím Postgres + Scraper v Dockeru..."
	docker-compose up -d postgres scraper
	@echo ">>> Čekám na healthcheck Postgresu..."
	@until docker exec realestate-db pg_isready -U postgres -d realestate_dev -q 2>/dev/null; do \
		printf '.'; sleep 2; \
	done
	@echo ""
	@echo ">>> Postgres je zdravý!"
	@echo ">>> Scraper API dostupné na: http://localhost:8001"

rebuild:
	@echo ">>> Přebuilduji scraper image (nové scrapery)..."
	docker-compose build --no-cache scraper
	docker-compose up -d --force-recreate scraper
	@echo ">>> Scraper restartován s novým image."

down:
	@echo ">>> Zastavuji všechny Docker kontejnery..."
	docker-compose down
	@echo ">>> Killuju lokální .NET procesy..."
	pkill -f "dotnet run.*RealEstate" 2>/dev/null || true
	@echo ">>> Hotovo."

# ---- .NET lokálně ----

api:
	@echo ">>> Spouštím .NET API na http://localhost:5001..."
	dotnet run --project src/RealEstate.Api --urls "http://localhost:5001" &
	@echo ">>> API spuštěno (background). PID: $$!"

app:
	@echo ">>> Spouštím Blazor App na http://localhost:5002..."
	dotnet run --project src/RealEstate.App --urls "http://localhost:5002" &
	@echo ">>> App spuštěna (background). PID: $$!"

# ---- Vše najednou ----

all: up
	@sleep 5
	@$(MAKE) api
	@sleep 3
	@$(MAKE) app
	@echo ""
	@echo "====================================="
	@echo "  Stack je nahoru!"
	@echo "  API:     http://localhost:5001"
	@echo "  App:     http://localhost:5002"
	@echo "  Scraper: http://localhost:8001"
	@echo "  PgAdmin: docker-compose up pgadmin"
	@echo "====================================="

# ---- Monitoring ----

logs:
	docker-compose logs -f scraper

status:
	@echo "=== Docker kontejnery ==="
	@docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}" | grep -E "realestate|NAME" || echo "(žádné)"
	@echo ""
	@echo "=== Lokální porty ==="
	@lsof -iTCP -sTCP:LISTEN -P 2>/dev/null | awk '$$9 ~ /:5001|:5002|:8001|:5432/ {print $$1, $$2, $$9}' | sort -u || echo "(žádné)"

# ---- Testování ----

test-scrape:
	@echo ">>> Triggeruji testovací scrape (REMAX + CENTURY21)..."
	curl -s -X POST http://localhost:8001/scrape/trigger \
		-H "Content-Type: application/json" \
		-d '{"source_codes": ["REMAX", "CENTURY21", "DELUXREALITY", "LEXAMO"], "full_rescan": false}' \
		| python3 -m json.tool || echo "Scraper API nedostupný (zkus: make up)"

test-all-scrapers:
	@echo ">>> Triggeruji full scrape všech zdrojů..."
	curl -s -X POST http://localhost:8001/scrape/trigger \
		-H "Content-Type: application/json" \
		-d '{"source_codes": null, "full_rescan": false}' \
		| python3 -m json.tool || echo "Scraper API nedostupný (zkus: make up)"

# ---- DB utils ----

seed:
	@echo ">>> Seeduji sources přímo do DB..."
	docker exec realestate-db psql -U postgres -d realestate_dev -c "\
		INSERT INTO re_realestate.sources (code, name, base_url, is_active, supports_url_scrape, supports_list_scrape, scraper_type) VALUES \
		('DELUXREALITY', 'DeluXreality Znojmo',       'https://deluxreality.cz',       true, true, true, 'Python'), \
		('LEXAMO',       'Lexamo Reality',             'https://www.lexamo.cz',         true, true, true, 'Python'), \
		('CENTURY21',    'CENTURY 21 Czech Republic',  'https://www.century21.cz',      true, true, true, 'Python'), \
		('PREMIAREALITY','PREMIA Reality s.r.o.',      'https://www.premiareality.cz',  true, true, true, 'Python'), \
		('HVREALITY',    'Horak & Vetchy reality',     'https://hvreality.cz',          true, true, true, 'Python'), \
		('NEMZNOJMO',    'Nemovitosti Znojmo',         'https://www.nemovitostiznojmo.cz', true, true, true, 'Python') \
		ON CONFLICT (code) DO NOTHING;" \
	&& echo ">>> Sources seeded."

db-connect:
	docker exec -it realestate-db psql -U postgres -d realestate_dev

db-stats:
	@docker exec realestate-db psql -U postgres -d realestate_dev -c \
		"SELECT s.code, s.name, COUNT(l.id) as listings FROM re_realestate.sources s \
		LEFT JOIN re_realestate.listings l ON l.source_id = s.id \
		GROUP BY s.code, s.name ORDER BY listings DESC;" 2>&1 | head -30
