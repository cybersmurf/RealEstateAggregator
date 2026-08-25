# Co ověřit a nasadit na sudgate

Doprovod k `PROJECT_ANALYSIS_2026-08-25.md`. Kódové opravy jsou hotové v repu;
tady je to, co jde udělat jen na serveru nebo co jsem nemohl ověřit zvenčí.

Seřazeno podle závažnosti. `[ ]` = k udělání.

---

## A. Ověření – rychlé, jen čtení

### A1. 🔴 Běží produkce s výchozím API klíčem?

Tohle mi bezpečnostní filtr nedovolil spustit, takže je to jediná věc z celé analýzy,
kterou jsem **neověřil**. Compose má `API_KEY=${API_KEY:-dev-key-change-me}`, a protože
nasazení jede jako `Development`, dosavadní pojistka v kódu se nikdy nespustila.

```bash
ssh sudgate 'docker inspect realestate-api \
  --format "{{range .Config.Env}}{{println .}}{{end}}" | grep API_KEY'
```

- [ ] Pokud vyjde `API_KEY=dev-key-change-me` → **je to klíč zapsaný v gitu**.
      Vygeneruj nový a zapiš do `/srv/realestate/.env`:
      ```bash
      openssl rand -base64 32
      ```
      Pozor: stejnou hodnotu potřebuje i služba `app` (`ScrapingApiKey`) — obě čtou `${API_KEY}`
      ze stejného `.env`, takže stačí jedno místo.
- [ ] Pokud je nastavený vlastní → hotovo, jen odškrtni.

---

### A2. 🔴 Zálohuje se databáze?

Nenašel jsem žádný backup skript ani cron — ani v tomhle repu, ani v `zupagate`.
Server je vyřazený notebook a v DB je 6 803 inzerátů včetně tvých hodnocení,
poznámek, analýz a embeddingů.

```bash
ssh sudgate 'ls -la /srv/backup* /var/backups 2>/dev/null; crontab -l; sudo crontab -l'
```

- [ ] Existuje záloha? Kde a jak stará?
- [ ] Pokud ne, minimální varianta (denní dump + rotace 14 dní):
      ```bash
      ssh sudgate 'sudo mkdir -p /srv/backup/postgres'
      ssh sudgate 'cat | sudo tee /usr/local/bin/backup-realestate.sh >/dev/null' <<'SH'
      #!/usr/bin/env bash
      set -euo pipefail
      DEST=/srv/backup/postgres
      docker exec realestate-db pg_dump -U postgres -Fc realestate_dev \
        > "$DEST/realestate_$(date +%F).dump"
      find "$DEST" -name 'realestate_*.dump' -mtime +14 -delete
      SH
      ssh sudgate 'sudo chmod +x /usr/local/bin/backup-realestate.sh'
      # cron 2:00, tj. hodinu před nočním scrapem
      ssh sudgate 'echo "0 2 * * * /usr/local/bin/backup-realestate.sh" | sudo crontab -'
      ```
- [ ] **Kopie mimo notebook** — dump na stejném disku nepomůže, když odejde disk.
      (rsync na Mac, Hetzner VPS, nebo cokoli jiného.)
- [ ] Jednou ověřit, že se dump nahraje do prázdné DB. Netestovaná záloha není záloha.

---

### A3. 🟠 Kolik místa zbývá?

Uploady fotek jdou do volume `uploads_data`, limit těla požadavku je 1 GB.

```bash
ssh sudgate 'df -h /; docker system df'
```

- [ ] Zbývá rozumná rezerva?
- [ ] Zvážit `docker system prune -af --filter "until=720h"`, pokud se hromadí staré image.

---

## B. Nasazení – Traefik

### B1. 🔴 Zavřít veřejné API BasicAuthem

Ověřeno zvenčí: `GET https://realestate.sudata.eu/api/sources` → **200 bez tokenu**.
Router `realestate-api` v `zupagate/config/traefik/dynamic/routes.yml` nemá žádný
middleware, zatímco Prometheus i Alertmanager hned vedle mají `monitoring-auth`.

Config jsem **záměrně needitoval** — placeholder s neplatným hashem, který by se omylem
nasadil, je horší než současný stav. Postup:

```bash
# 1) hash hesla (dvojité $$ kvůli escapování v Traefiku)
htpasswd -nb petr 'ZVOL_SI_HESLO' | sed 's/\$/$$/g'
```

- [ ] Do `zupagate/config/traefik/dynamic/routes.yml` přidat do sekce `middlewares:`:
      ```yaml
          realestate-auth:
            basicAuth:
              users:
                - "petr:$$2y$$05$$...vlastní hash..."
      ```
- [ ] A do routeru `realestate-api` (frontend `realestate` nechat beze změny):
      ```yaml
          realestate-api:
            rule: "Host(`realestate.sudata.eu`) && PathPrefix(`/api`)"
            entryPoints: [websecure]
            service: realestate-api
            priority: 100
            middlewares: [realestate-auth]     # ← přidat
            tls: {}
      ```
- [ ] Zkopírovat na server (file provider má `watch: true`, restart není potřeba):
      ```bash
      scp config/traefik/dynamic/routes.yml sudgate:/tmp/ && \
        ssh sudgate 'sudo cp /tmp/routes.yml /srv/traefik/dynamic/routes.yml'
      ```

**Ověřit po nasazení:**
```bash
curl -s -o /dev/null -w "%{http_code}\n" https://realestate.sudata.eu/api/sources   # čekáme 401
curl -s -o /dev/null -w "%{http_code}\n" https://realestate.sudata.eu/              # čekáme 200
```
- [ ] API vrací 401, frontend 200.
- [ ] **Frontend pořád funguje** — Blazor volá API interně přes `http://realestate-api:8080`
      (Docker network, mimo Traefik), takže by se ho BasicAuth neměl dotknout.
      Zkontrolovat hlavně **zobrazování fotek** — ty jdou přes `ApiPublicUrl`.
      Kdyby se fotky rozbily, je to tímhle.
- [ ] MCP server (`realestate-mcp`) volá `http://realestate-api:8080` — taky interně, mělo by být OK.
      Ověřit jedním dotazem přes Claude Desktop.

---

## C. Nasazení – kód

Nejdřív commit a push (bez toho se na server nedostane nic — viz `AGENTS.md`).

### C1. 🔴 LEXAMO – opravený scraper

**Diagnóza:** scraper nebyl rozbitý. Parser funguje, kontejner na `lexamo.cz` dosáhne
(ověřeno z obou stran). Problém byl, že LEXAMO neplnil pole `district`, takže geo filtr
viděl jen název obce („Vrbovec") a porovnával ho proti `target_districts: ["Znojmo", …]`.
Neshoda → zahodit. Takhle padalo **všech 29** inzerátů, včetně ~12 z okresu Znojmo.

Oprava vytahuje okres ze slugu URL (`…-okres-znojmo-…`). Ověřeno proti živému webu:
**0 → 12 inzerátů projde filtrem.** Zbylá odmítnutí jsou správná (okres Třebíč, byty
vypnuté configem, jeden pozemek nad cenovým stropem).

- [ ] Nasadit a spustit jen LEXAMO:
      ```bash
      make deploy-api        # nebo rebuild scraperu podle toho, jak deployuješ
      ssh sudgate 'curl -s -X POST http://localhost:8001/v1/scrape \
        -H "Content-Type: application/json" \
        -d "{\"source_codes\":[\"LEXAMO\"],\"full_rescan\":false}"'
      ```
- [ ] Ověřit:
      ```bash
      ssh sudgate 'curl -s http://localhost:8001/v1/health/scrapers' | python3 -m json.tool | head -20
      ```
      Čekáme `overall: ok` a u LEXAMO `active_count` kolem 12.

**Podezření k prověření:** stejná chyba může tiše ubírat inzeráty i jinde. Zdroje
s podezřele nízkým počtem: ZNOJMOREALITY (4), HVREALITY (7), DELUXREALITY (10).
Stojí za to zkontrolovat, jestli plní `district`.

### C2. 🟠 Přepnout na Production

V repu je nový `docker-compose.prod.yml`. Zapne kontrolu API klíče a vypne Swagger.

```bash
ssh sudgate 'cd /srv/realestate && docker compose \
  -f docker-compose.yml -f docker-compose.prod.yml up -d api'
```

- [ ] **Nejdřív A1** — pokud je `API_KEY` výchozí, API po přepnutí **odmítne nastartovat**
      (to je záměr, ne chyba).
- [ ] Ověřit, že API naběhlo: `ssh sudgate 'docker logs realestate-api --tail 30'`
- [ ] Ověřit, že frontend jede: `curl -s -o /dev/null -w "%{http_code}\n" https://realestate.sudata.eu/`

Co jsem kvůli tomuhle přepnutí musel v kódu ošetřit, aby to nerozbilo produkci:
- `UseHttpsRedirection()` je pryč — TLS řeší Traefik a redirect by rozbil interní
  HTTP volání `App → http://realestate-api:8080`.
- Bootstrap schématu už není uvnitř `if (IsDevelopment())` — jinak by prázdná DB
  v Production nastartovala bez jediné tabulky.
- Přibyl `UseForwardedHeaders()` — bez něj vidí aplikace jako klientskou IP kontejner
  Traefiku, což mimo jiné rozbíjí rate limiting.

### C3. 🟠 Monitoring aplikačního zdraví

Prometheus dnes scrapuje jen `prometheus`, `node`, `cadvisor`, `traefik` — samé
infrastrukturní metriky. Proto mohl LEXAMO 6 dní mlčky ležet: kontejner běžel a byl zdravý.

- [ ] Přidat scrape target na `/v1/health/scrapers` (blackbox exporter nebo malý
      textfile exporter, který ten JSON převede na metriku).
- [ ] Alert `ScraperSourceDead` na `stale_or_dead > 0` po dobu 6 h → Slack
      (Alertmanager už do Slacku posílá).
- [ ] Doplnit `/health/db`, `/health/ollama`, `/health/scraper` z API.
- [ ] Zvážit `/metrics` přímo v .NET API (`prometheus-net`) — počty inzerátů,
      stáří posledního scrape.

---

## D. Volitelné

- [ ] **MCP na loopback.** `docker-compose.prod.yml` sváže port 8002 na `127.0.0.1`.
      Z internetu ho drží UFW + DOCKER-USER, ale explicitní bind nespoléhá na firewall.
      Pozor: kdyby ses na MCP připojoval z LAN, tohle to zavře.
- [ ] **Sjednotit secrets.** `secrets/` i `src/RealEstate.Api/secrets/` mají stejné
      Google Drive soubory. OAuth token se při refreshi zapisuje jen do jednoho —
      commit `43182c5` ukazuje, že se to už jednou projevilo.
- [ ] **`zupagate/hetzner_cred.txt`** leží v gitovém repu. Ověřit, že ho `.gitignore` pokrývá
      (má 99 bajtů, takže spíš ne) a případně přesunout do `/etc/sudgate/secrets`
      k ostatním credentials.
- [ ] **Úklid disku na Macu.** 2,9 GB v pracovním adresáři, z toho 1,4 GB `bin/` včetně
      rekurzivně vnořených `bin/Release/net10.0/bin/Debug/…` přes 100 úrovní (láme `grep -r`).
      363 MB je osiřelých po smazaném projektu `RealEstate.Background`.

---

## Co je hotové v repu

| Změna | Soubor |
|---|---|
| LEXAMO – okres ze slugu URL (0 → 12 inzerátů) | `scraper/core/scrapers/lexamo_scraper.py` |
| Pojistka na API klíč platí mimo Development (byl to mrtvý kód) | `Program.cs` |
| Rate limiting skutečně per IP (`AddPolicy` + partition) | `Program.cs` |
| `UseForwardedHeaders`, odstraněn `UseHttpsRedirection` | `Program.cs` |
| Bootstrap schématu už není vázaný na Development | `Program.cs` |
| Konstantní porovnání API klíče (`FixedTimeEquals`) | `Program.cs` |
| `PageSize` clamp – 200 search / 5 000 export | `ListingService.cs` |
| `AsNoTracking()` na read-only cestě | `ListingRepository.cs` |
| PostGIS + uuid-ossp v EF modelu | `RealEstateDbContext.cs` |
| Init DB jen s extensions (nemountovat celý `./scripts`) | `docker/initdb/`, `docker-compose.yml` |
| DB healthcheck ověřuje schéma, ne jen `pg_isready` | `docker-compose.yml` |
| Produkční override | `docker-compose.prod.yml` |
| AutoMapper pryč (nepoužíval se, high CVE), `Microsoft.OpenApi` na 2.12.2 | `RealEstate.Api.csproj` |
| Smazán nenamapovaný `ScrapingPlaywrightEndpoints` | – |
| Mrtvá OneDrive větev | `mcp/server.py` |
| CI: build + testy + audit CVE + compileall | `.github/workflows/ci.yml` |
| +20 testů (155 C# / 106 pytest, vše zelené) | `ListingPagingTests.cs`, `test_parsers.py` |

**Nedotčeno záměrně:** squash EF migrací (potřebuje diff proti produkčnímu schématu),
`BaseScraper` refaktoring, rozpad `ListingDetail.razor`, autentizace v aplikaci.
