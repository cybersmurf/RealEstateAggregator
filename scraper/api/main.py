"""
FastAPI application for Real Estate Scraper.
Provides REST endpoints to trigger and monitor scraping jobs.
"""
from contextlib import asynccontextmanager
from fastapi import FastAPI, BackgroundTasks, HTTPException
from uuid import uuid4, UUID
from datetime import datetime
import yaml
from pathlib import Path
import os
import logging
import asyncio

from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger
from apscheduler.triggers.interval import IntervalTrigger

from .schemas import ScrapeTriggerRequest, ScrapeTriggerResponse, ScrapeJob
from core.runner import run_scrape_job
from core.database import init_db_manager, get_db_manager
from core.geocoding import bulk_geocode, geocode_address
from core.ruian_service import lookup_ruian_address, bulk_ruian_lookup
from core import notifications

logger = logging.getLogger(__name__)

# Globální scheduler instance
_scheduler: AsyncIOScheduler | None = None

def get_scheduler() -> AsyncIOScheduler:
    if _scheduler is None:
        raise RuntimeError("Scheduler není inicializován")
    return _scheduler


async def _scheduled_scrape_all(full_rescan: bool = False) -> None:
    """Naplánovaný scraping všech aktivních zdrojů."""
    try:
        db_manager = get_db_manager()

        async with db_manager.acquire() as conn:
            rows = await conn.fetch(
                "SELECT code FROM re_realestate.sources WHERE is_active = true ORDER BY code"
            )
        source_codes = [r["code"] for r in rows]

        if not source_codes:
            logger.warning("[Scheduler] Žádné aktivní zdroje k scrapování")
            return

        job_id = uuid4()
        request = ScrapeTriggerRequest(source_codes=source_codes, full_rescan=full_rescan)
        await db_manager.create_scrape_job(job_id=job_id, source_codes=source_codes, full_rescan=full_rescan)
        logger.info(
            "[Scheduler] Spouštím naplánovaný scraping: %s zdrojů, job_id=%s, full_rescan=%s",
            len(source_codes),
            job_id,
            full_rescan,
        )
        await run_scrape_job(job_id, request)
    except asyncio.CancelledError:
        logger.warning("[Scheduler] Scraping job cancelled (app shutdown?)")
        raise
    except Exception as exc:
        logger.exception("[Scheduler] Scraping job failed: %r", exc)


@asynccontextmanager
async def lifespan(app: FastAPI):
    """
    FastAPI lifespan context manager.
    Handles startup and shutdown events.
    
    Replaces deprecated @app.on_event("startup"/"shutdown") decorators.
    """
    # ============================================================================
    # STARTUP
    # ============================================================================
    try:
        # Načti config ze settings.yaml
        config_path = Path(__file__).parent.parent / "config" / "settings.yaml"
        
        if not config_path.exists():
            raise FileNotFoundError(f"Config file not found: {config_path}")
        
        with open(config_path, "r") as f:
            config = yaml.safe_load(f)
        
        if not config:
            raise ValueError("Empty config file")
        
        # Inicializuj DB manager
        db_config = config.get("database", {})
        
        if not db_config:
            raise ValueError("Missing 'database' section in config")
        
        db_config = {
            **db_config,
            "host": os.getenv("DB_HOST", db_config.get("host", "localhost")),
            "port": int(os.getenv("DB_PORT", db_config.get("port", 5432))),
            "database": os.getenv("DB_NAME", db_config.get("database", "realestate_dev")),
            "user": os.getenv("DB_USER", db_config.get("user", "postgres")),
            "password": os.getenv("DB_PASSWORD", db_config.get("password", "dev")),
        }

        db_manager = init_db_manager(
            host=db_config.get("host"),
            port=db_config.get("port"),
            database=db_config.get("database"),
            user=db_config.get("user"),
            password=db_config.get("password"),
            min_size=db_config.get("min_connections", 5),
            max_size=db_config.get("max_connections", 20),
            source_cache_ttl_seconds=db_config.get("source_cache_ttl_seconds", 3600),
        )
        
        # Připoj k databázi
        await db_manager.connect()
        logger.info(f"✓ Database connected to {db_config.get('host')}:{db_config.get('port')}/{db_config.get('database')}")
        print(f"✓ Database connected to {db_config.get('host')}:{db_config.get('port')}/{db_config.get('database')}")

        # ── Scheduler ──────────────────────────────────────────────────────
        global _scheduler
        _scheduler = AsyncIOScheduler(timezone="Europe/Prague")

        sched_cfg = config.get("scheduler", {})
        if sched_cfg.get("enabled", True):
            # Denní quick scraping – každý den ve 3:00 ráno
            daily_cron = sched_cfg.get("daily_cron", "0 3 * * *")
            _scheduler.add_job(
                _scheduled_scrape_all,
                CronTrigger.from_crontab(daily_cron, timezone="Europe/Prague"),
                id="daily_scrape",
                name="Denní scraping (všechny zdroje)",
                replace_existing=True,
                kwargs={"full_rescan": False},
            )
            # Týdenní full rescan – v neděli v 2:00
            weekly_cron = sched_cfg.get("weekly_cron", "0 2 * * 0")
            _scheduler.add_job(
                _scheduled_scrape_all,
                CronTrigger.from_crontab(weekly_cron, timezone="Europe/Prague"),
                id="weekly_full_rescan",
                name="Týdenní full rescan",
                replace_existing=True,
                kwargs={"full_rescan": True},
            )
            _scheduler.start()
            logger.info(f"✓ Scheduler spuštěn | denní={daily_cron} | weekly={weekly_cron}")
            print(f"✓ Scheduler spuštěn | denní={daily_cron} | týdenní={weekly_cron}")
        else:
            logger.info("Scheduler je vypnut (scheduler.enabled=false v settings.yaml)")

        # ── Notifikace ─────────────────────────────────────────────────────
        notif_cfg = config.get("notifications", {})
        notifications.configure(notif_cfg.get("slack_webhook_url") or "")

    except Exception as exc:
        logger.error(f"❌ Startup failed: {exc}")
        print(f"❌ Startup failed: {exc}")
        raise
    
    # Yield control to FastAPI app
    yield
    
    # ============================================================================
    # SHUTDOWN
    # ============================================================================
    try:
        if _scheduler and _scheduler.running:
            _scheduler.shutdown(wait=False)
            logger.info("✓ Scheduler zastaven")
        db_manager = get_db_manager()
        await db_manager.disconnect()
        logger.info("✓ Database disconnected")
        print("✓ Database disconnected")
    except Exception as exc:
        logger.error(f"Error during shutdown: {exc}")


app = FastAPI(
    title="RealEstate Scraper API",
    description="API pro spouštění a monitorování scraping jobů realitních inzerátů",
    version="1.0.0",
    lifespan=lifespan  # 🔥 Nový přístup - lifespan context manager
)


@app.get("/")
async def root():
    """Health check endpoint."""
    return {
        "service": "RealEstate Scraper API",
        "status": "running",
        "version": "1.0.0"
    }


@app.post("/v1/scrape/run", response_model=ScrapeTriggerResponse)
async def trigger_scrape(
    request: ScrapeTriggerRequest,
    background_tasks: BackgroundTasks,
) -> ScrapeTriggerResponse:
    """
    Spustí scraping job v pozadí.
    
    Args:
        request: ScrapeTriggerRequest s optional source_codes a full_rescan flag
        background_tasks: FastAPI BackgroundTasks pro async spuštění
        
    Returns:
        ScrapeTriggerResponse s job_id a statusem
    """
    job_id = uuid4()
    
    # Vytvoř DB record pro job
    db_manager = get_db_manager()
    await db_manager.create_scrape_job(
        job_id=job_id,
        source_codes=request.source_codes or [],
        full_rescan=request.full_rescan
    )

    # Spustit async v backgroundu, aby API odpovědělo hned
    background_tasks.add_task(run_scrape_job, job_id, request)

    return ScrapeTriggerResponse(
        job_id=job_id,
        status="Queued",
        message="Scraping job enqueued.",
    )


@app.get("/v1/scrape/jobs/{job_id}", response_model=ScrapeJob)
async def get_scrape_job(job_id: str) -> ScrapeJob:
    """
    Získá status scraping jobu podle job_id.
    
    Args:
        job_id: UUID jobu jako string
        
    Returns:
        ScrapeJob s aktuálním stavem
        
    Raises:
        HTTPException 404 pokud job neexistuje
    """
    try:
        job_uuid = UUID(job_id)
    except ValueError:
        raise HTTPException(status_code=400, detail="Invalid job_id format")
    
    db_manager = get_db_manager()
    job_data = await db_manager.get_scrape_job(job_uuid)
    
    if not job_data:
        raise HTTPException(status_code=404, detail=f"Job {job_id} not found")
    
    # Překonvertuj DB data na ScrapeJob Pydantic model
    return ScrapeJob(
        job_id=job_data['id'],
        source_codes=list(job_data['source_codes']),  # PostgreSQL array → Python list
        full_rescan=job_data['full_rescan'],
        created_at=job_data['created_at'],
        started_at=job_data['started_at'],
        finished_at=job_data['finished_at'],
        status=job_data['status'],
        progress=job_data['progress'],
        error_message=job_data['error_message'],
    )


@app.get("/v1/scrape/jobs", response_model=list[ScrapeJob])
async def list_scrape_jobs(limit: int = 50, status: str = None) -> list[ScrapeJob]:
    """
    Vrátí seznam scraping jobů seřazených chronologicky (nejnovější první).
    
    Args:
        limit: Maximální počet jobů k vrácení (default 50)
        status: Volitelný filtr podle statusu (Queued, Running, Succeeded, Failed)
    
    Returns:
        List[ScrapeJob] - joby z databáze
    """
    db_manager = get_db_manager()
    jobs_data = await db_manager.list_scrape_jobs(limit=limit, status=status)
    
    return [
        ScrapeJob(
            job_id=job['id'],
            source_codes=list(job['source_codes']),
            full_rescan=job['full_rescan'],
            created_at=job['created_at'],
            started_at=job['started_at'],
            finished_at=job['finished_at'],
            status=job['status'],
            progress=job['progress'],
            error_message=job['error_message'],
        )
        for job in jobs_data
    ]


# ============================================================================
# GEOCODING ENDPOINTS
# ============================================================================

@app.post("/v1/geocode/bulk")
async def trigger_bulk_geocode(background_tasks: BackgroundTasks, batch_size: int = 50):
    """Spustí dávkové geokódování inzerátů bez GPS souřadnic pomocí Nominatim."""
    batch_size = min(max(1, batch_size), 200)
    db_manager = get_db_manager()

    async def _run_geocode():
        count = await bulk_geocode(db_manager, batch_size=batch_size)
        logger.info(f"Bulk geocoding dokončen: {count} inzerátů geokódováno")

    background_tasks.add_task(_run_geocode)
    return {"status": "started", "batch_size": batch_size, "message": "Geocoding běží na pozadí"}


@app.get("/v1/geocode/single")
async def geocode_single(address: str, country: str = "CZ"):
    """Geokóduje jedinou adresu pomocí Nominatim – pro testování."""
    result = await geocode_address(address, country=country)
    if result:
        lat, lon = result
        return {"latitude": lat, "longitude": lon, "address": address}
    raise HTTPException(status_code=404, detail=f"Adresa nenalezena: '{address}'")


@app.get("/v1/geocode/stats")
async def geocode_stats():
    """Statistika geokódování – kolik inzerátů má / nemá GPS souřadnice."""
    db_manager = get_db_manager()
    async with db_manager.acquire() as conn:
        row = await conn.fetchrow(
            """
            SELECT
                COUNT(*) AS total,
                COUNT(*) FILTER (WHERE latitude IS NOT NULL) AS with_coords,
                COUNT(*) FILTER (WHERE latitude IS NULL AND is_active = true) AS active_without_coords,
                COUNT(*) FILTER (WHERE geocode_source = 'scraper') AS from_scraper,
                COUNT(*) FILTER (WHERE geocode_source = 'nominatim') AS from_nominatim
            FROM re_realestate.listings
            """
        )
    return {
        "total": row["total"],
        "with_coords": row["with_coords"],
        "active_without_coords": row["active_without_coords"],
        "from_scraper": row["from_scraper"],
        "from_nominatim": row["from_nominatim"],
    }


# ══════════════════════════════════════════════════════════════════════════════
# ČÚZK / RUIAN – Katastr nemovitostí
# ══════════════════════════════════════════════════════════════════════════════

@app.get("/v1/ruian/single")
async def ruian_single(address: str, municipality: str = ""):
    """
    Vyhledá adresu v RUIAN a vrátí kód adresního místa + odkaz na nahlížení do KN.
    Parametry:
      address    – adresa (nebo jen název obce)
      municipality – název obce (preferovaný vstup, přesnější výsledky)
    """
    result = await lookup_ruian_address(address, municipality=municipality or None)
    return result


@app.post("/v1/ruian/bulk")
async def ruian_bulk(
    background_tasks: BackgroundTasks,
    batch_size: int = 50,
    overwrite_not_found: bool = False,
):
    """
    Hromadné vyhledávání RUIAN pro inzeráty bez katastrálních dat.
    Výsledky se ukládají do tabulky listing_cadastre_data.
    ⚠️ Pomalé kvůli rate limitingu (1 req/s). Běží na pozadí.
    """
    db_manager = get_db_manager()
    batch_size = min(max(batch_size, 1), 200)

    async def _run():
        stats = await bulk_ruian_lookup(
            db_manager,
            batch_size=batch_size,
            overwrite_not_found=overwrite_not_found,
        )
        logger.info(f"RUIAN bulk hotovo: {stats}")

    background_tasks.add_task(_run)
    return {
        "status": "started",
        "batch_size": batch_size,
        "overwrite_not_found": overwrite_not_found,
        "message": "RUIAN bulk lookup běží na pozadí",
    }


@app.get("/v1/ruian/stats")
async def ruian_stats():
    """Statistika RUIAN / katastrálních dat v DB."""
    db_manager = get_db_manager()
    async with db_manager.acquire() as conn:
        row = await conn.fetchrow(
            """
            SELECT
                COUNT(*) AS total_listings,
                COUNT(lcd.id) AS with_cadastre_data,
                COUNT(lcd.id) FILTER (WHERE lcd.fetch_status = 'found') AS ruian_found,
                COUNT(lcd.id) FILTER (WHERE lcd.fetch_status = 'not_found') AS ruian_not_found,
                COUNT(lcd.id) FILTER (WHERE lcd.fetch_status = 'error') AS ruian_error,
                COUNT(lcd.id) FILTER (WHERE lcd.fetch_status = 'manual') AS manual
            FROM re_realestate.listings l
            LEFT JOIN re_realestate.listing_cadastre_data lcd ON lcd.listing_id = l.id
            WHERE l.is_active = true
            """
        )

    return {
        "total_active_listings": row["total_listings"],
        "with_cadastre_data": row["with_cadastre_data"],
        "ruian_found": row["ruian_found"],
        "ruian_not_found": row["ruian_not_found"],
        "ruian_error": row["ruian_error"],
        "manual": row["manual"],
    }


# ══════════════════════════════════════════════════════════════════════════════
# SCHEDULER – správa naplánovaných úloh
# ══════════════════════════════════════════════════════════════════════════════

@app.get("/v1/schedule/jobs")
async def list_scheduled_jobs():
    """Vrátí seznam naplánovaných úloh a jejich příští spuštění."""
    if _scheduler is None:
        return {"enabled": False, "jobs": []}

    jobs = []
    for job in _scheduler.get_jobs():
        jobs.append({
            "id": job.id,
            "name": job.name,
            "next_run": job.next_run_time.isoformat() if job.next_run_time else None,
            "trigger": str(job.trigger),
        })
    return {
        "enabled": _scheduler.running,
        "timezone": "Europe/Prague",
        "jobs": jobs,
    }


@app.post("/v1/schedule/trigger-now")
async def trigger_scheduled_scrape_now(
    background_tasks: BackgroundTasks,
    full_rescan: bool = False,
):
    """
    Okamžitě spustí naplánovaný scraping (stejně jako by ho pustil scheduler).
    Užitečné pro ruční spuštění bez čekání na plánovaný čas.
    """
    background_tasks.add_task(_scheduled_scrape_all, full_rescan)
    return {
        "status": "started",
        "full_rescan": full_rescan,
        "message": f"Naplánovaný scraping spuštěn okamžitě (full_rescan={full_rescan})",
    }


@app.put("/v1/schedule/jobs/{job_id}/pause")
async def pause_scheduled_job(job_id: str):
    """Pozastaví naplánovanou úlohu (daily_scrape nebo weekly_full_rescan)."""
    if _scheduler is None:
        raise HTTPException(status_code=503, detail="Scheduler není aktivní")
    job = _scheduler.get_job(job_id)
    if not job:
        raise HTTPException(status_code=404, detail=f"Job '{job_id}' nenalezen")
    _scheduler.pause_job(job_id)
    return {"job_id": job_id, "status": "paused"}


@app.put("/v1/schedule/jobs/{job_id}/resume")
async def resume_scheduled_job(job_id: str):
    """Obnoví pozastavenou naplánovanou úlohu."""
    if _scheduler is None:
        raise HTTPException(status_code=503, detail="Scheduler není aktivní")
    job = _scheduler.get_job(job_id)
    if not job:
        raise HTTPException(status_code=404, detail=f"Job '{job_id}' nenalezen")
    _scheduler.resume_job(job_id)
    return {"job_id": job_id, "status": "resumed"}


@app.put("/v1/schedule/jobs/{job_id}/cron")
async def update_job_cron(job_id: str, cron: str):
    """
    Změní cron výraz naplánované úlohy za běhu.
    Formát: '0 3 * * *' (minuta hodina den měsíc den_týdne)
    """
    if _scheduler is None:
        raise HTTPException(status_code=503, detail="Scheduler není aktivní")
    job = _scheduler.get_job(job_id)
    if not job:
        raise HTTPException(status_code=404, detail=f"Job '{job_id}' nenalezen")
    try:
        new_trigger = CronTrigger.from_crontab(cron, timezone="Europe/Prague")
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Neplatný cron výraz: {e}")
    _scheduler.reschedule_job(job_id, trigger=new_trigger)
    updated_job = _scheduler.get_job(job_id)
    return {
        "job_id": job_id,
        "cron": cron,
        "next_run": updated_job.next_run_time.isoformat() if updated_job.next_run_time else None,
    }


# ══════════════════════════════════════════════════════════════════════════════
# SCRAPER HEALTH – monitoring mrtvých / zastaralých zdrojů
# ══════════════════════════════════════════════════════════════════════════════

@app.get("/v1/health/scrapers")
async def scraper_health(stale_days: int = 3):
    """
    Vrátí zdraví všech aktivních zdrojů.
    Zdroje bez aktualizace déle než `stale_days` dní jsou označeny jako stale/dead.

    Args:
        stale_days: Počet dní bez aktualizace = problém (default 3)
    """
    db_manager = get_db_manager()
    async with db_manager.acquire() as conn:
        rows = await conn.fetch(
            """
            SELECT
                s.code,
                s.name,
                COUNT(*) FILTER (WHERE l.is_active = true)  AS active_count,
                COUNT(*) FILTER (WHERE l.is_active = false) AS inactive_count,
                MAX(l.last_seen_at)                          AS last_seen_at,
                EXTRACT(EPOCH FROM (NOW() - MAX(l.last_seen_at))) / 86400 AS days_stale
            FROM re_realestate.sources s
            LEFT JOIN re_realestate.listings l ON l.source_id = s.id
            WHERE s.is_active = true
            GROUP BY s.code, s.name
            ORDER BY days_stale DESC NULLS FIRST
            """,
        )

    sources = []
    stale_sources = []
    for r in rows:
        days = float(r["days_stale"]) if r["days_stale"] is not None else None
        entry = {
            "code": r["code"],
            "name": r["name"],
            "active_count": r["active_count"],
            "inactive_count": r["inactive_count"],
            "last_seen_at": r["last_seen_at"].isoformat() if r["last_seen_at"] else None,
            "days_stale": round(days, 1) if days is not None else None,
            "status": (
                "dead" if days is None or (days >= stale_days and r["active_count"] == 0)
                else "stale" if days >= stale_days
                else "ok"
            ),
        }
        sources.append(entry)
        if entry["status"] in ("dead", "stale"):
            stale_sources.append(entry)

    # Odešli Slack notifikaci pokud jsou stale zdroje
    if stale_sources:
        await notifications.notify_stale_sources(stale_sources)

    ok_count = sum(1 for s in sources if s["status"] == "ok")
    return {
        "overall": "ok" if not stale_sources else "degraded",
        "total_sources": len(sources),
        "ok": ok_count,
        "stale_or_dead": len(stale_sources),
        "stale_threshold_days": stale_days,
        "sources": sources,
    }
