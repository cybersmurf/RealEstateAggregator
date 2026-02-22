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

from .schemas import ScrapeTriggerRequest, ScrapeTriggerResponse, ScrapeJob
from core.runner import run_scrape_job
from core.database import init_db_manager, get_db_manager

logger = logging.getLogger(__name__)


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
