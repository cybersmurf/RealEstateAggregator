# Deploy Services Skill

## Description
Deploy RealEstateAggregator services using Docker Compose.

## Usage
```bash
gh copilot suggest "deploy all services to production"
```

## Steps

1. **Prerequisites check**
   ```bash
   docker --version
   docker-compose --version
   git status  # Ensure clean working tree
   ```

2. **Build Docker images**
   ```bash
   # From project root
   docker-compose build --no-cache
   ```

3. **Start services**
   ```bash
   # Development (with hot reload)
   docker-compose up -d
   
   # Production (optimized)
   docker-compose -f docker-compose.yml -f docker-compose.prod.yml up -d
   ```

4. **Verify services**
   ```bash
   # Check all containers running
   docker-compose ps
   
   # Expected output:
   # postgres   - Up, :5432
   # api        - Up, :5001
   # app        - Up, :5002
   # scraper    - Up, :8001
   ```

5. **Health checks**
   ```bash
   # Database
   docker exec -it realestate-db pg_isready -U postgres
   
   # API
   curl http://localhost:5001/api/sources
   
   # Blazor App
   curl http://localhost:5002
   
   # Scraper
   curl http://localhost:8001/v1/scrape/sources
   ```

6. **Apply database migrations**
   ```bash
   docker-compose exec api dotnet ef database update
   
   # Or manually
   docker exec -it realestate-db psql -U postgres -d realestate_dev -f /docker-entrypoint-initdb.d/init-db.sql
   ```

7. **Verify data seeding**
   ```bash
   docker exec -it realestate-db psql -U postgres -d realestate_dev
   ```
   ```sql
   SELECT code, name, is_active FROM re_realestate.sources;
   -- Should show REMAX, MMREALITY, PRODEJMETO
   ```

8. **Monitor logs**
   ```bash
   # All services
   docker-compose logs -f
   
   # Specific service
   docker-compose logs -f api
   docker-compose logs -f scraper
   ```

## Service URLs

**Development:**
- PostgreSQL: `localhost:5432`
- .NET API: `http://localhost:5001`
- Blazor App: `http://localhost:5002`
- Python Scraper: `http://localhost:8001`

**Production:**
- Use environment-specific URLs from `.env` file
- Configure reverse proxy (nginx/traefik) for HTTPS

## Configuration

### Environment Variables (.env file)
```env
# Database
POSTGRES_USER=postgres
POSTGRES_PASSWORD=secure_password_here
POSTGRES_DB=realestate_prod

# .NET API
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5001
ConnectionStrings__RealEstate=Host=postgres;Port=5432;Database=realestate_prod;Username=postgres;Password=secure_password_here

# Scraper API
SCRAPER_API_URL=http://scraper:8001

# Optional: OpenAI for semantic search
OPENAI_API_KEY=sk-...
```

### Docker Compose Override (docker-compose.prod.yml)
```yaml
version: '3.9'
services:
  postgres:
    restart: always
    volumes:
      - postgres_data_prod:/var/lib/postgresql/data
  
  api:
    restart: always
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
  
  app:
    restart: always
  
  scraper:
    restart: always

volumes:
  postgres_data_prod:
```

## Common Commands

```bash
# Stop all services
docker-compose down

# Stop and remove volumes (DANGER: data loss!)
docker-compose down -v

# Rebuild specific service
docker-compose build api
docker-compose up -d api

# View logs since timestamp
docker-compose logs --since 2024-02-22T10:00:00

# Restart single service
docker-compose restart scraper

# Execute command in container
docker-compose exec api dotnet --version
docker-compose exec scraper python --version

# Database backup
docker exec realestate-db pg_dump -U postgres realestate_dev > backup_$(date +%Y%m%d).sql

# Database restore
docker exec -i realestate-db psql -U postgres -d realestate_dev < backup_20240222.sql
```

## Deployment Checklist

**Pre-deployment:**
- [ ] All tests pass (`dotnet test`)
- [ ] No uncommitted changes (`git status`)
- [ ] Environment variables configured (`.env` file)
- [ ] Secrets rotated (database passwords, API keys)
- [ ] Database backup created

**Deployment:**
- [ ] Docker images built without cache
- [ ] Services started with `docker-compose up -d`
- [ ] All containers show "Up" status
- [ ] Health checks pass (curl endpoints)
- [ ] Database migrations applied
- [ ] Seed data verified

**Post-deployment:**
- [ ] Monitor logs for errors
- [ ] Verify scraper runs successfully
- [ ] Test critical user flows (search, view listing)
- [ ] Check disk space (`df -h`)
- [ ] Set up monitoring (Prometheus, Grafana)

## Rollback

```bash
# Stop current deployment
docker-compose down

# Checkout previous version
git checkout <previous-commit-hash>

# Rebuild and restart
docker-compose build
docker-compose up -d

# Restore database backup (if needed)
docker exec -i realestate-db psql -U postgres -d realestate_dev < backup_previous.sql
```

## Monitoring

### Resource Usage
```bash
docker stats
docker-compose top
```

### Disk Space
```bash
# Check PostgreSQL data size
docker exec realestate-db du -sh /var/lib/postgresql/data

# Clean unused Docker resources
docker system prune -a --volumes
```

### Database Performance
```sql
-- Slow queries
SELECT query, mean_exec_time, calls 
FROM pg_stat_statements 
ORDER BY mean_exec_time DESC 
LIMIT 10;

-- Table sizes
SELECT schemaname, tablename, pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) 
FROM pg_tables 
WHERE schemaname = 're_realestate' 
ORDER BY pg_total_relation_size(schemaname||'.'||tablename) DESC;
```

## Related Files
- `docker-compose.yml`
- `docker-compose.prod.yml` (create if needed)
- `.env` (not in git, create manually)
- `scripts/init-db.sql`
- `Dockerfile` (in each project if multi-stage)

## Troubleshooting

**Error:** "port already allocated"
- Solution: `docker-compose down && docker-compose up -d`

**Error:** "database connection failed"
- Solution: Check `ConnectionStrings__RealEstate` in environment

**Error:** "scraper API unreachable from .NET API"
- Solution: Use service name `http://scraper:8001` not `localhost:8001`

**Error:** "out of disk space"
- Solution: `docker system prune -a --volumes` (⚠️ removes unused volumes!)
