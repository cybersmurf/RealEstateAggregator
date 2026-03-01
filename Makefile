# ============================================================
# RealEstateAggregator – Makefile
# Vše běží v Dockeru – make up je jediný příkaz potřebný
# ============================================================

.PHONY: help up down clean restart rebuild rebuild-api rebuild-app rebuild-scraper \
        logs logs-api logs-app logs-scraper logs-db \
        status ps db db-stats test scrape scrape-full secrets-sync

help:
	@echo "================================================="
	@echo "  RealEstateAggregator – příkazy"
	@echo "================================================="
	@echo "  make up              – Start celého stacku v Dockeru"
	@echo "  make down            – Stop kontejnerů (data zachována)"
	@echo "  make clean           – Stop + smazání volumes (reset DB!)"
	@echo "  make restart         – Restart bez rebuild"
	@echo "  make rebuild         – Build + restart api, app, scraper"
	@echo "  make rebuild-api     – Build + restart jen API"
	@echo "  make rebuild-app     – Build + restart jen Blazor App"
	@echo "  make rebuild-scraper – Build + restart jen Python scraper"
	@echo "  make status          – Health check všech služeb"
	@echo "  make ps              – Stav Docker kontejnerů"
	@echo "  make logs            – Živé logy všech služeb"
	@echo "  make db              – psql konzole"
	@echo "  make db-stats        – Počty inzerátů dle zdroje"
	@echo "  make test            – Spustí unit testy"
	@echo "  make scrape          – Inkrementální scrape všech zdrojů"
	@echo "  make scrape-full     – Plný rescan všech zdrojů"
	@echo "  make secrets-sync    – Zkopíruje Google Drive secrets do API kontejneru"
	@echo "================================================="

# ---- Start / Stop --------------------------------------------------------------

up:
	@echo ">>> Spouštím celý stack v Dockeru..."
	docker-compose up -d
	$(MAKE) secrets-sync
	@echo ""
	@echo "  App:     http://localhost:5002"
	@echo "  API:     http://localhost:5001"
	@echo "  Scraper: http://localhost:8001"
	@echo "  DB:      localhost:5432"

down:
	docker-compose stop

clean:
	@echo ">>> POZOR: maže data databáze!"
	docker-compose down -v

restart:
	docker-compose restart

# ---- Rebuild -------------------------------------------------------------------

rebuild:
	docker-compose build api app scraper
	docker-compose up -d --force-recreate api app scraper
	$(MAKE) secrets-sync

rebuild-api:
	docker-compose build api
	docker-compose up -d --force-recreate api
	$(MAKE) secrets-sync

rebuild-app:
	docker-compose build app
	docker-compose up -d --force-recreate app

rebuild-scraper:
	docker-compose build scraper
	docker-compose up -d --force-recreate scraper

# ---- Secrets -----------------------------------------------------------------

# Colima bind mount pro ./secrets nefunguje spolehlivě – kopírujeme ručně.
# Volá se automaticky po 'make up' a 'make rebuild-api'.
secrets-sync:
	@echo ">>> Sychronizuji Google Drive secrets do API kontejneru..."
	@docker cp secrets/google-drive-token.json realestate-api:/app/secrets/google-drive-token.json 2>/dev/null && echo "  google-drive-token.json OK" || echo "  WARN: google-drive-token.json nenalezen (Drive OAuth nebude fungovat)"
	@docker cp secrets/google-drive-sa.json realestate-api:/app/secrets/google-drive-sa.json 2>/dev/null && echo "  google-drive-sa.json OK" || echo "  WARN: google-drive-sa.json nenalezen (Drive SA nebude fungovat)"

# ---- Logy ----------------------------------------------------------------------

logs:
	docker-compose logs -f --tail=50

logs-api:
	docker-compose logs -f --tail=100 api

logs-app:
	docker-compose logs -f --tail=100 app

logs-scraper:
	docker-compose logs -f --tail=100 scraper

logs-db:
	docker-compose logs -f --tail=50 postgres

# ---- Status --------------------------------------------------------------------

ps:
	@docker-compose ps

status:
	@echo "=== Docker kontejnery ==="
	@docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}" | grep realestate || echo "(zadne)"
	@echo ""
	@echo "=== API health ==="
	@curl -s http://localhost:5001/health 2>/dev/null | python3 -m json.tool || echo "  nereaguje"
	@echo ""
	@echo "=== App ==="
	@curl -s -o /dev/null -w "  HTTP %{http_code}\n" http://localhost:5002/ 2>/dev/null || echo "  nereaguje"
	@echo ""
	@echo "=== Scraper ==="
	@curl -s http://localhost:8001/health 2>/dev/null | python3 -m json.tool || echo "  nereaguje"

# ---- DB ------------------------------------------------------------------------

db:
	docker exec -it realestate-db psql -U postgres -d realestate_dev

db-stats:
	@docker exec realestate-db psql -U postgres -d realestate_dev -c \
	  "SELECT source_code, COUNT(*) AS pocet FROM re_realestate.listings WHERE is_active=true GROUP BY source_code ORDER BY pocet DESC;"

# ---- Testy ---------------------------------------------------------------------

test:
	dotnet test tests/RealEstate.Tests --no-build -v quiet

# ---- Scraping ------------------------------------------------------------------

scrape:
	curl -s -X POST http://localhost:8001/v1/scrape/run \
	  -H "Content-Type: application/json" \
	  -d '{"source_codes":["REMAX","MMR","PRODEJMETO","ZNOJMOREALITY","SREALITY","IDNES","NEMZNOJMO","HVREALITY","PREMIAREALITY","DELUXREALITY","LEXAMO","CENTURY21"],"full_rescan":false}' \
	  | python3 -m json.tool

scrape-full:
	curl -s -X POST http://localhost:8001/v1/scrape/run \
	  -H "Content-Type: application/json" \
	  -d '{"source_codes":["REMAX","MMR","PRODEJMETO","ZNOJMOREALITY","SREALITY","IDNES","NEMZNOJMO","HVREALITY","PREMIAREALITY","DELUXREALITY","LEXAMO","CENTURY21"],"full_rescan":true}' \
	  | python3 -m json.tool
