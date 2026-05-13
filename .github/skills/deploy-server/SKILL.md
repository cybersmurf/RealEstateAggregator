---
name: deploy-server
description: "Deploy RealEstate app to production server (192.168.11.2). Use when deploying Blazor app or API changes, when running docker builds, or when troubleshooting server build failures. Handles git stash/pop workflow for server local overrides."
argument-hint: "What to deploy: app | api | both"
---

# Deploy to Production Server

## Prerequisites
- SSH access to `petrsramek@192.168.11.2`
- Changes committed and pushed to `master`

## ⚠️ Server Local Override Warning
Server's `docker-compose.yml` has LOCAL (uncommitted) changes:
- Ollama URL = `http://192.168.11.199:11434`
- No ARM64 platform override

**Always use `git stash && git pull && git stash pop`** – never `git pull` alone.

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
ssh petrsramek@192.168.11.2 'cd /srv/realestate && git stash && git pull && git stash pop && docker compose build app && docker compose up -d --no-deps app'
```

**API only:**
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && git stash && git pull && git stash pop && docker compose build api && docker compose up -d --no-deps api'
```

**Both (app + api):**
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && git stash && git pull && git stash pop && docker compose build app api && docker compose up -d --no-deps app api'
```

### Step 3: Verify
```bash
ssh petrsramek@192.168.11.2 'docker inspect realestate-app --format "Started: {{.State.StartedAt}}" && docker compose ps'
```

## Common Issues

| Problem | Symptom | Fix |
|---------|---------|-----|
| DNS failure | `NU1301 Unable to load service index` / `Temporary failure resolving api.nuget.org` | `network: host` already set in docker-compose.yml – do NOT remove |
| Local changes conflict | `git pull` fails: "local changes overwritten" | `git stash && git pull && git stash pop` |
| Stale Docker cache | Old code despite rebuild | `docker compose build --no-cache app api` |
| Port conflict | 5001/5002 already bound | `lsof -i :5001 -P -n` → kill local dotnet process |

## Secrets (Google Drive)
Bind mount doesn't work on Colima. After rebuild, sync secrets manually:
```bash
ssh petrsramek@192.168.11.2 'cd /srv/realestate && docker cp secrets/google-drive-sa.json realestate-api:/app/secrets/'
```
Or run `make secrets-sync` locally (syncs via docker cp).
