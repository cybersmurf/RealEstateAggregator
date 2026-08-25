# ============================================================
# RealEstateAggregator – Makefile
# Produkce: sudgate (192.168.11.2 / realestate.sudata.eu)
# Lokál: make up / make down pro vývoj
# ============================================================

# --- Server konfigurace -------------------------------------------------------
SERVER      := petrsramek@192.168.11.2
REMOTE_DIR  := /srv/realestate
COMPOSE     := docker compose -p realestate

.PHONY: help up down clean restart rebuild rebuild-api rebuild-app rebuild-scraper \
        logs logs-api logs-app logs-scraper logs-db \
        status ps db db-stats test scrape scrape-full secrets-sync \
        deploy-api deploy-app deploy-both \
        server-status server-logs-api server-logs-app server-logs-scraper \
        server-db server-db-stats server-scrape server-scrape-full \
        server-secrets-sync server-restart-api server-restart-app

help:
	@echo "======================================================="
	@echo "  RealEstateAggregator – příkazy"
	@echo "  Produkce: $(SERVER) (realestate.sudata.eu)"
	@echo "======================================================="
	@echo ""
	@echo "  --- DEPLOY (server) ---"
	@echo "  make deploy-api      – push → build → restart API na serveru"
	@echo "  make deploy-app      – push → build → restart App na serveru"
	@echo "  make deploy-both     – push → build → restart API+App na serveru"
	@echo "  make server-status   – Stav kontejnerů + health check na serveru"
	@echo "  make server-logs-api – Živé logy API na serveru"
	@echo "  make server-db       – psql konzole na serveru"
	@echo "  make server-db-stats – Počty inzerátů dle zdroje (server)"
	@echo "  make server-scrape   – Inkrementální scrape (server)"
	@echo "  make server-scrape-full – Plný rescan (server)"
	@echo "  make server-secrets-sync – Sync GDrive secrets do kontejneru (server)"
	@echo ""
	@echo "  --- LOKÁLNÍ vývoj ---"
	@echo "  make up              – Start celého stacku lokálně"
	@echo "  make down            – Stop lokálních kontejnerů"
	@echo "  make clean           – Stop + smazání volumes (reset DB!)"
	@echo "  make rebuild-api     – Build + restart jen API (lokálně)"
	@echo "  make rebuild-app     – Build + restart jen Blazor App (lokálně)"
	@echo "  make rebuild-scraper – Build + restart jen scraper (lokálně)"
	@echo "  make status          – Health check lokálních služeb"
	@echo "  make db              – psql konzole (lokálně)"
	@echo "  make test            – Spustí unit testy"
	@echo "  make scrape          – Inkrementální scrape (lokálně)"
	@echo "  make scrape-full     – Plný rescan (lokálně)"
	@echo "======================================================="

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
	$(COMPOSE) stop

clean:
	@echo ">>> POZOR: maže data databáze!"
	$(COMPOSE) down -v

restart:
	$(COMPOSE) restart

# ---- Rebuild (lokální) --------------------------------------------------------

rebuild:
	$(COMPOSE) build api app scraper
	$(COMPOSE) up -d --force-recreate api app scraper
	$(MAKE) secrets-sync

rebuild-api:
	$(COMPOSE) build api
	$(COMPOSE) up -d --force-recreate api
	$(MAKE) secrets-sync

rebuild-app:
	$(COMPOSE) build app
	$(COMPOSE) up -d --force-recreate app

rebuild-scraper:
	$(COMPOSE) build scraper
	$(COMPOSE) up -d --force-recreate scraper

# ---- Secrets -----------------------------------------------------------------

# Colima bind mount pro ./secrets nefunguje spolehlivě – kopírujeme ručně.
# Volá se automaticky po 'make up' a 'make rebuild-api'.
secrets-sync:
	@echo ">>> Sychronizuji Google Drive secrets do API kontejneru..."
	@docker cp secrets/google-drive-token.json realestate-api:/app/secrets/google-drive-token.json 2>/dev/null && echo "  google-drive-token.json OK" || echo "  WARN: google-drive-token.json nenalezen (Drive OAuth nebude fungovat)"
	@docker cp secrets/google-drive-sa.json realestate-api:/app/secrets/google-drive-sa.json 2>/dev/null && echo "  google-drive-sa.json OK" || echo "  WARN: google-drive-sa.json nenalezen (Drive SA nebude fungovat)"

# ---- Logy ----------------------------------------------------------------------

logs:
	$(COMPOSE) logs -f --tail=50

logs-api:
	$(COMPOSE) logs -f --tail=100 api

logs-app:
	$(COMPOSE) logs -f --tail=100 app

logs-scraper:
	$(COMPOSE) logs -f --tail=100 scraper

logs-db:
	$(COMPOSE) logs -f --tail=50 postgres

# ---- Status --------------------------------------------------------------------

ps:
	@$(COMPOSE) ps

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
	  -d '{"source_codes":["REMAX","MMR","PRODEJMETO","ZNOJMOREALITY","SREALITY","IDNES","NEMZNOJMO","HVREALITY","PREMIAREALITY","DELUXREALITY","LEXAMO","CENTURY21","REAS","BAZOS"],"full_rescan":false}' \
	  | python3 -m json.tool

scrape-full:
	curl -s -X POST http://localhost:8001/v1/scrape/run \
	  -H "Content-Type: application/json" \
	  -d '{"source_codes":["REMAX","MMR","PRODEJMETO","ZNOJMOREALITY","SREALITY","IDNES","NEMZNOJMO","HVREALITY","PREMIAREALITY","DELUXREALITY","LEXAMO","CENTURY21","REAS","BAZOS"],"full_rescan":true}' \
	  | python3 -m json.tool

# ---- Deploy (server) ----------------------------------------------------------

# Předpoklad: lokální změny jsou commitnuty a pushnuty na master.
# Server provede git stash + pull + stash pop pro zachování lokálních override (Ollama URL atd.).

DEPLOY_BASE = cd $(REMOTE_DIR) && git stash && git pull && git stash pop

# Na serveru MUSÍ jít oba compose soubory dohromady. Samotné `docker compose -p realestate`
# vezme jen docker-compose.yml, kde je ASPNETCORE_ENVIRONMENT=Development natvrdo –
# každý deploy by tak tiše vrátil API z Production do Development a zapnul Swagger.
COMPOSE_SRV := docker compose -p realestate -f docker-compose.yml -f docker-compose.prod.yml

# Počká, až kontejner ohlásí healthy. Bez toho ověření curluje dřív, než API
# stihne nastartovat (healthcheck má start_period 30 s), a deploy hlásí 502
# i u naprosto v pořádku proběhlého nasazení.
define wait_healthy
	@echo ">>> Čekám na healthy: $(1)"
	@ssh $(SERVER) 'for i in $$(seq 1 40); do \
	  s=$$(docker inspect -f "{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}" $(1) 2>/dev/null); \
	  [ "$$s" = "healthy" ] && echo "   $(1): healthy" && exit 0; \
	  [ "$$s" = "none" ] && echo "   $(1): bez healthchecku, pokračuji" && exit 0; \
	  sleep 3; \
	done; echo "   $(1): NEDOSÁHL healthy"; docker logs --tail 30 $(1); exit 1'
endef

deploy-api:
	@echo ">>> Deploy API na $(SERVER)..."
	ssh $(SERVER) '$(DEPLOY_BASE) && $(COMPOSE_SRV) build api && $(COMPOSE_SRV) up -d --no-deps api && docker cp $(REMOTE_DIR)/secrets/google-drive-sa.json realestate-api:/app/secrets/ && docker cp $(REMOTE_DIR)/secrets/google-drive-token.json realestate-api:/app/secrets/ && echo "DEPLOY API OK"'
	$(call wait_healthy,realestate-api)
	@echo ">>> Ověření..."
	@ssh $(SERVER) "curl -sf -o /dev/null -w 'API HTTP %{http_code}\n' https://realestate.sudata.eu/api/sources"

deploy-app:
	@echo ">>> Deploy App na $(SERVER)..."
	ssh $(SERVER) '$(DEPLOY_BASE) && $(COMPOSE_SRV) build app && $(COMPOSE_SRV) up -d --no-deps app && echo "DEPLOY APP OK"'
	$(call wait_healthy,realestate-app)
	@echo ">>> Ověření..."
	@ssh $(SERVER) "curl -sf -o /dev/null -w 'App HTTP %{http_code}\n' https://realestate.sudata.eu/"

deploy-scraper:
	@echo ">>> Deploy scraperu na $(SERVER)..."
	ssh $(SERVER) '$(DEPLOY_BASE) && $(COMPOSE_SRV) build scraper && $(COMPOSE_SRV) up -d --no-deps scraper && echo "DEPLOY SCRAPER OK"'
	@echo ">>> Ověření..."
	@ssh $(SERVER) "curl -sf http://localhost:8001/v1/health/scrapers" \
	  | python3 -c "import json,sys; d=json.load(sys.stdin); print('scrapery: %s, ok=%d/%d, stale/dead=%d' % (d['overall'], d['ok'], d['total_sources'], d['stale_or_dead']))"

deploy-both:
	@echo ">>> Deploy API+App na $(SERVER)..."
	ssh $(SERVER) '$(DEPLOY_BASE) && $(COMPOSE_SRV) build api app && $(COMPOSE_SRV) up -d --no-deps api app && docker cp $(REMOTE_DIR)/secrets/google-drive-sa.json realestate-api:/app/secrets/ && docker cp $(REMOTE_DIR)/secrets/google-drive-token.json realestate-api:/app/secrets/ && echo "DEPLOY OK"'
	$(call wait_healthy,realestate-api)
	@echo ">>> Ověření..."
	@ssh $(SERVER) "curl -sf -o /dev/null -w 'API HTTP %{http_code}\n' https://realestate.sudata.eu/api/sources"
	@ssh $(SERVER) "curl -sf -o /dev/null -w 'App HTTP %{http_code}\n' https://realestate.sudata.eu/"

# ---- Server monitoring ---------------------------------------------------------

server-status:
	@echo "=== Kontejnery ==="
	@ssh $(SERVER) "sudo docker ps --filter 'name=realestate' --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'"
	@echo ""
	@echo "=== API health ==="
	@ssh $(SERVER) "curl -sf https://realestate.sudata.eu/api/sources | python3 -m json.tool | head -5" || echo "  nereaguje"
	@echo ""
	@echo "=== App ==="
	@ssh $(SERVER) "curl -sf -o /dev/null -w '  HTTPS %{http_code}\n' https://realestate.sudata.eu/" || echo "  nereaguje"

server-logs-api:
	ssh $(SERVER) "sudo docker logs -f --tail=100 realestate-api"

server-logs-app:
	ssh $(SERVER) "sudo docker logs -f --tail=50 realestate-app"

server-logs-scraper:
	ssh $(SERVER) "sudo docker logs -f --tail=100 realestate-scraper"

server-restart-api:
	ssh $(SERVER) "sudo docker compose -p realestate -f $(REMOTE_DIR)/docker-compose.yml restart api"

server-restart-app:
	ssh $(SERVER) "sudo docker compose -p realestate -f $(REMOTE_DIR)/docker-compose.yml restart app"

# ---- Server DB -----------------------------------------------------------------

server-db:
	ssh -t $(SERVER) "sudo docker exec -it realestate-db psql -U postgres -d realestate_dev"

server-db-stats:
	@ssh $(SERVER) "sudo docker exec realestate-db psql -U postgres -d realestate_dev -c \
	  \"SELECT source_code, COUNT(*) AS pocet FROM re_realestate.listings WHERE is_active=true GROUP BY source_code ORDER BY pocet DESC;\""

# ---- Server Scraping -----------------------------------------------------------

server-scrape:
	@echo ">>> Inkrementální scrape na serveru..."
	@ssh $(SERVER) "curl -s -X POST http://localhost:8001/v1/scrape/run \
	  -H 'Content-Type: application/json' \
	  -d '{\"source_codes\":[\"REMAX\",\"MMR\",\"PRODEJMETO\",\"ZNOJMOREALITY\",\"SREALITY\",\"IDNES\",\"NEMZNOJMO\",\"HVREALITY\",\"PREMIAREALITY\",\"DELUXREALITY\",\"LEXAMO\",\"CENTURY21\",\"REAS\",\"BAZOS\"],\"full_rescan\":false}' \
	  | python3 -m json.tool"

server-scrape-full:
	@echo ">>> Plný rescan na serveru..."
	@ssh $(SERVER) "curl -s -X POST http://localhost:8001/v1/scrape/run \
	  -H 'Content-Type: application/json' \
	  -d '{\"source_codes\":[\"REMAX\",\"MMR\",\"PRODEJMETO\",\"ZNOJMOREALITY\",\"SREALITY\",\"IDNES\",\"NEMZNOJMO\",\"HVREALITY\",\"PREMIAREALITY\",\"DELUXREALITY\",\"LEXAMO\",\"CENTURY21\",\"REAS\",\"BAZOS\"],\"full_rescan\":true}' \
	  | python3 -m json.tool"

# ---- Server Secrets ------------------------------------------------------------

server-secrets-sync:
	@echo ">>> Sync secrets na serveru..."
	@ssh $(SERVER) "sudo docker cp $(REMOTE_DIR)/secrets/google-drive-sa.json realestate-api:/app/secrets/ && echo '  google-drive-sa.json OK'"
	@ssh $(SERVER) "sudo docker cp $(REMOTE_DIR)/secrets/google-drive-token.json realestate-api:/app/secrets/ 2>/dev/null && echo '  google-drive-token.json OK' || echo '  WARN: google-drive-token.json neni na serveru'"
