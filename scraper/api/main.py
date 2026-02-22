"""
FastAPI application for Real Estate Scraper.
Provides REST endpoints to trigger and monitor scraping jobs.
"""
from fastapi import FastAPI, BackgroundTasks, HTTPException
from uuid import uuid4
from datetime import datetime
from typing import Dict
import yaml
from pathlib import Path
import os

from .schemas import ScrapeTriggerRequest, ScrapeTriggerResponse, ScrapeJob
from core.runner import run_scrape_job
from core.database import init_db_manager, get_db_manager

app = FastAPI(
    title="RealEstate Scraper API",
    description="API pro spouštění a monitorování scraping jobů realitních inzerátů",
    version="1.0.0"
)

# V jednoduché verzi držíme joby jen v paměti
# V produkci bys použil Redis, PostgreSQL, nebo jiné persistent storage
SCRAPE_JOBS: Dict[str, ScrapeJob] = {}


@app.on_event("startup")
async def startup_event():
    """Inicializace při startu aplikace."""
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
        )
        
        # Připoj k databázi
        await db_manager.connect()
        print(f"✓ Database connected to {db_config.get('host')}:{db_config.get('port')}/{db_config.get('database')}")
        
    except Exception as exc:
        print(f"❌ Startup failed: {exc}")
        raise


@app.on_event("shutdown")
async def shutdown_event():
    """Cleanup při ukončení aplikace."""
    try:
        db_manager = get_db_manager()
        await db_manager.disconnect()
        print("✓ Database disconnected")
    except:
        pass


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
    job = ScrapeJob(
        job_id=job_id,
        source_codes=request.source_codes,
        full_rescan=request.full_rescan,
        created_at=datetime.utcnow(),
        status="Queued",
    )
    SCRAPE_JOBS[str(job_id)] = job

    # Spustit async v backgroundu, aby API odpovědělo hned
    background_tasks.add_task(run_scrape_job, job_id, request, SCRAPE_JOBS)

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
    job = SCRAPE_JOBS.get(job_id)
    if not job:
        raise HTTPException(status_code=404, detail=f"Job {job_id} not found")
    return job


@app.get("/v1/scrape/jobs", response_model=list[ScrapeJob])
async def list_scrape_jobs() -> list[ScrapeJob]:
    """
    Vrátí seznam všech scraping jobů.
    
    Returns:
        List[ScrapeJob] - všechny joby v paměti
    """
    return list(SCRAPE_JOBS.values())
