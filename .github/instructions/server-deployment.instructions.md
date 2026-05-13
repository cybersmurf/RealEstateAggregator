---
description: "Use when deploying changes to the production server (192.168.11.2 / realstate.sudata.eu). Covers git stash/pop workflow, Docker rebuild, and known server quirks."
---

# Server Deployment Workflow

## Server Info
- **LAN IP**: `192.168.11.2`
- **Public**: `realstate.sudata.eu` (HTTPS via reverse proxy)
- **Project path**: `/srv/realestate/`
- **SSH**: `ssh petrsramek@192.168.11.2`

## ⚠️ Critical: Server Has Local docker-compose.yml Changes
Server's `docker-compose.yml` contains uncommitted local overrides:
- Ollama URL: `http://192.168.11.199:11434` (LAN machine with GPU)
- No `platform: linux/arm64/v8` (server is amd64)

**NEVER** commit or overwrite these. Always use the `git stash` pattern below.

## Standard Deploy (Blazor App)

```bash
ssh petrsramek@192.168.11.2 '
  cd /srv/realestate &&
  git stash &&
  git pull &&
  git stash pop &&
  docker compose build app &&
  docker compose up -d --no-deps app
'
```

## Rebuild API

```bash
ssh petrsramek@192.168.11.2 '
  cd /srv/realestate &&
  git stash &&
  git pull &&
  git stash pop &&
  docker compose build api &&
  docker compose up -d --no-deps api
'
```

## Rebuild Both (app + api)

```bash
ssh petrsramek@192.168.11.2 '
  cd /srv/realestate &&
  git stash &&
  git pull &&
  git stash pop &&
  docker compose build app api &&
  docker compose up -d --no-deps app api
'
```

## DNS Fix (Already Applied)
Docker BuildKit containers cannot reach `127.0.0.53` (systemd-resolved stub on host loopback).
**Fix is already in `docker-compose.yml`**: `network: host` in build sections for `app` and `api`.
Do NOT remove this – it's required for `dotnet restore` and `apt-get` to resolve DNS during build.

## Verify Deployment

```bash
ssh petrsramek@192.168.11.2 '
  docker compose ps &&
  docker inspect realestate-app --format "Started: {{.State.StartedAt}}"
'
```

## Service Overview

| Container | Port | Rebuild when |
|---|---|---|
| `realestate-app` | 5002 | Any `.razor` / Blazor change |
| `realestate-api` | 5001 | Any `.cs` API/service change |
| `realestate-scraper` | 8001 | Python scraper changes |
| `realestate-mcp` | 8002 | MCP server changes |
| `realestate-db` | 5432 | Never (data volume) |

## Force No-Cache Rebuild

Use only if incremental build produces stale artifacts:
```bash
docker compose build --no-cache app api
```

## Scraper (Python – no Docker rebuild needed)

```bash
ssh petrsramek@192.168.11.2 '
  cd /srv/realestate &&
  docker compose restart scraper
'
```
