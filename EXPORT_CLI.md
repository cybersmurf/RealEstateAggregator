# Real Estate Export CLI - Quick Reference

**Datum**: 22. února 2026  
**Status**: ✅ TESTING (PostgreSQL + Seed Data Working)

---

## 📋 Co je vytvořeno

### ✅ Infrastruktura
- PostgreSQL 15 s pgvector (Docker image: pgvector/pgvector:pg15)
- Kompletní schéma re_realestate s 6 tabulkami
- EF Core migrations hotové (InitialSchema migration)
- Seed data: 3 sources (REMAX, M&M Reality, Prodejme.to), 4 sample listings

### ✅ Export Nástrojů
- **scripts/export.sh** - Single listing export (Markdown)
- **scripts/export-batch.sh** - Batch export s filtery (Markdown)
- Both scripts optimalizovány pro přímý PostgreSQL přístup (bez EF Core complexity)

### 📊 Seed Data
```
Sources (3):
- REMAX (RE/MAX Czech Republic)
- MMR (M&M Reality)  
- PRODEJMETO (Prodejme.to)

Listings (4):
1. Útulný byt 3+1 v Brně - REMAX (4.8M Kč, 80 m²)
2. Mezonetový byt 2+1 v Praze - REMAX (6.2M Kč, 90 m²)
3. Rodinný dům v Znojmě - MMR (5.5M Kč, 160 m²)
4. Studio byt v Praze 1 - PRODEJMETO (18K Kč/měsíc, 35 m²)
```

---

## 🚀 Použití

### 1. Single Listing Export

```bash
# Export jednoho listingu do Markdown
./scripts/export.sh <listing-id> [format] [output-dir]

# Příklady:
./scripts/export.sh 178c77cb-3662-4063-b0b6-60ca114b96dc
./scripts/export.sh 178c77cb-3662-4063-b0b6-60ca114b96dc markdown ./my_exports
```

**Výstup**: Markdown soubor s metadatou, cenou, plochou, popisem

### 2. Batch Export

```bash
# Export více listingů s filtry
./scripts/export-batch.sh [region] [limit] [format] [output-dir]

# Příklady:
./scripts/export-batch.sh "Jihomoravský" 10 markdown ./exports
./scripts/export-batch.sh "" 5 markdown ./exports    # všechny, limit 5
./scripts/export-batch.sh "Praha" 20 markdown ./exports
```

**Výstup**: Markdown soubor s indexem všech inzerátů + metadata

---

## 📊 Sample Output

### Single Listing Export
```markdown
# Útulný byt 3+1 v Brně - ulice Nádraží

## 📋 Metadata
| Parametr | Hodnota |
|----------|---------|
| **ID** | `178c77cb-3662-4063-b0b6-60ca114b96dc` |
| **Zdroj** | REMAX |
| **Region** | Jihomoravský |

## 💰 Cena a Plocha
| Parametr | Hodnota |
|----------|---------|
| **Cena** | 4800000.00 Kč |
| **Plocha** | 80 m² |
| **Pokoje** | 3 |

## 📝 Popis
Prodám útulný byt 3+1 v centru Brna...
```

### Batch Export
```markdown
# Real Estate Export - Batch Report

## 1. Rodinný dům v Jihomoravském kraji
| Parametr | Hodnota |
| **Cena** | 5500000.00 Kč |
| **Plocha** | 160 m² |

## 2. Útulný byt 3+1 v Brně
| Parametr | Hodnota |
| **Cena** | 4800000.00 Kč |
| **Plocha** | 80 m² |
```

---

## 🔧 Technické Detaily

### PostgreSQL Connection
```
Host: localhost:5432
Database: realestate_dev
Username: postgres
Password: dev
Schema: re_realestate
```

### Docker Commands
```bash
# Start PostgreSQL
docker-compose up -d postgres

# View logs
docker-compose logs postgres

# Execute query
docker exec realestate-db psql -U postgres -d realestate_dev -c "SELECT count(*) FROM re_realestate.listings;"

# Stop
docker-compose down
```

### Database Schema
```
Tables:
- sources (3 records)
- listings (4 records)
- listing_photos (4 records)
- user_listing_state (0)
- analysis_jobs (0)
- scrape_runs (0)
```

---

## 🎯 Next Steps

### Phase 1: Core Export (✅ DONE)
- [x] PostgreSQL database setup
- [x] Seed initial data
- [x] Create export scripts
- [x] Test single listing export
- [x] Test batch export

### Phase 2: RealEstate.Export CLI (🚧 IN PROGRESS)
- [ ] Fix EF Core EntityFrameworkCore version conflict
- [ ] Implement C# export-listing command using EF Core
- [ ] Implement C# export-batch command
- [ ] Add JSON/HTML export formats (currently only Markdown in bash)

### Phase 3: AI Integration (📋 BACKLOG)
- [ ] Validate Markdown export format for Copilot/Perplexity prompts
- [ ] Test with actual Copilot Chat analysis
- [ ] Implement MudBlazor UI with filters
- [ ] Add PredicateBuilder for advanced filtering

### Phase 4: Python Scraper Integration (📋 BACKLOG)
- [ ] Connect Python scraper to database
- [ ] Persist scraper results to listings table
- [ ] Implement schedule-based scraping

---

## 📝 Known Issues

### 1. RealEstate.Export C# CLI
**Issue**: EntityFrameworkCore.Relational version conflict (10.0.0 vs 10.0.3)  
**Root Cause**: Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0 requires EF Core 10.0.0, but Infrastructure requires 10.0.3  
**Workaround**: Using bash scripts (scripts/export.sh, scripts/export-batch.sh) for now  
**Fix**: Wait for Npgsql 10.0.3 release or downgrade Infrastructure to EF Core 10.0.0

### 2. Batch Export Formatting
**Issue**: Titles with special characters (é, ř, etc.) in filenames need escaping  
**Impact**: Low - exports work, filenames are just truncated  
**Fix**: URL-encode filenames if needed

---

## 🚀 Running Export Examples

```bash
# Setup (one-time)
cd /Users/petrsramek/Projects/RealEstateAggregator
docker-compose up -d postgres
# Wait 15 seconds
cat scripts/seed-data.sql | docker exec -i realestate-db psql -U postgres -d realestate_dev

# Get listing IDs
docker exec realestate-db psql -U postgres -d realestate_dev -c \
  "SELECT id, title FROM re_realestate.listings LIMIT 5;"

# Export single listing
./scripts/export.sh 178c77cb-3662-4063-b0b6-60ca114b96dc markdown ./exports

# Export batch
./scripts/export-batch.sh "Jihomoravský" 10 markdown ./exports

# View results
ls -la exports/
cat exports/*.md | head -30
```

---

## 📞 Quick Help

```bash
# For export.sh
./scripts/export.sh --help
# Usage: ./scripts/export.sh <listing-id> [format] [output-dir]

# For export-batch.sh
./scripts/export-batch.sh --help
# Usage: ./scripts/export-batch.sh [region] [limit] [format] [output-dir]
```

---

**Status**: Ready for AI integration with Copilot/Perplexity/Gemini  
**Last Updated**: 22. února 2026, 17:20 CET
