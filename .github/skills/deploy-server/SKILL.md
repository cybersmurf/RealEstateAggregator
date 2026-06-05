---
name: deploy-server
description: "Deploy RealEstate app to production server (192.168.11.2). Use when deploying Blazor app or API changes, when running docker builds, or when troubleshooting server build failures. Handles git stash/pop workflow for server local overrides."
argument-hint: "What to deploy: app | api | both"
---

# Deploy to Production Server

## Prerequisites
- SSH access to `petrsramek@192.168.11.2`
- Changes committed and pushed to `master`

## ⚠️ KRITICKÉ: Vždy použij `-p realestate`
Na serveru je adresář `/srv/realestate` → default project name by byl `realestateaggregator` → jiná síť i volume → API by se nepřipojilo k existující DB!

**Všechny `docker compose` příkazy na serveru MUSÍ mít `-p realestate`.**

## ⚠️ NIKDY `docker rm -f` na api/app
`docker rm -f` smaže container filesystem včetně `/app/wwwroot/uploads/` kde jsou fotky!
Používej VÝHRADNĚ `docker compose -p realestate up -d --no-deps` — to provede graceful replace.

## ⚠️ Server Local Override Warning
Server's `docker-compose.yml` has LOCAL (uncommitted) changes:
- Ollama URL = `http://192.168.11.199:11434`
- No ARM64 platform override

**Always use `git stash && git pull && git stash pop`** – never `git pull` alone.

## ⚠️ Docker outbound network (UFW / DOCKER-USER)

If scrapers fail with `ConnectTimeout` from inside containers but `curl` from the host works,
install the persistent systemd unit (survives reboot):

```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && bash scripts/fix-docker-network.sh'
```

This updates `/etc/systemd/system/docker-iptables.service` to whitelist bridge subnets
`172.18–172.20` (realestate compose network) and disables the broken legacy
`docker-iptables-fix.service`.

## Deployment Steps

### Step 1: Commit & push locally
```bash
git add <changed-files>
git commit -m "feat/fix: <description>"
git push
```

### Step 2: Deploy to server

**Blazor App only:**
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && git stash && git pull && git stash pop && docker compose -p realestate build app && docker compose -p realestate up -d --no-deps app'
```

**API only:**
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && git stash && git pull && git stash pop && docker compose -p realestate build api && docker compose -p realestate up -d --no-deps api'
```

**Both (app + api):**
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && git stash && git pull && git stash pop && docker compose -p realestate build app api && docker compose -p realestate up -d --no-deps app api'
```

### Step 3: Sync secrets (vždy po rebuildu api)
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && docker cp secrets/google-drive-sa.json realestate-api:/app/secrets/'
```

### Step 4: Ověřit traefik síť
Nové/restarted containery musí být v `traefik-network` (routing přes `/srv/traefik/dynamic/routes.yml`):
```bash
ssh petrsramek@192.168.11.2 'sudo docker network connect traefik-network realestate-app 2>/dev/null; sudo docker network connect traefik-network realestate-api 2>/dev/null; echo done'
```
(Příkazy vrátí chybu "already exists" pokud je síť již připojena — to je OK, `2>/dev/null` to potlačí.)

### Step 5: Verify
```bash
ssh petrsramek@192.168.11.2 "sudo docker ps --filter name=realestate --format '{{.Names}}: {{.Status}}'"
ssh petrsramek@192.168.11.2 "curl -sf -o /dev/null -w 'HTTP %{http_code}\n' https://realestate.sudata.eu/api/sources"
```

## Common Issues

| Problem | Symptom | Fix |
|---------|---------|-----|
| DNS failure | `NU1301 Unable to load service index` | `network: host` already set in docker-compose.yml – do NOT remove |
| Local changes conflict | `git pull` fails | `git stash && git pull && git stash pop` |
| Stale Docker cache | Old code despite rebuild | `docker compose -p realestate build --no-cache app api` |
| Wrong project / wrong DB | API starts but 0 listings | Použij `-p realestate`; ověř `docker volume ls | grep realestate` |
| Traefik 404 | HTTPS vrací 404 nebo default cert | `sudo docker network connect traefik-network realestate-app realestate-api` |
| Secrets chybí | Drive analýzy se nezobrazují | `docker cp secrets/google-drive-sa.json realestate-api:/app/secrets/` |
| Fotky 404 po rebuild | stored_url ukazuje na /uploads/ soubory co neexistují | `UPDATE listing_photos SET stored_url=NULL WHERE original_url IS NOT NULL` |

## Secrets (Google Drive)
Bind mount `./secrets:/app/secrets` **nefunguje** na Colima (volume empty). Používej docker cp:
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && docker cp secrets/google-drive-sa.json realestate-api:/app/secrets/'
```
Nebo lokálně: `make secrets-sync`.
