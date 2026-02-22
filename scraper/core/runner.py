"""
Job runner for scraping tasks.
Orchestrates individual scrapers and manages job lifecycle.
"""
import asyncio
import logging
import yaml
from pathlib import Path
from typing import Optional, List, Dict, Any
from uuid import UUID
from datetime import datetime

from api.schemas import ScrapeTriggerRequest
from core.database import get_db_manager

logger = logging.getLogger(__name__)


def _load_scraper_config() -> Dict[str, Any]:
    """Load scraper configuration from settings.yaml."""
    config_path = Path(__file__).parent.parent / "config" / "settings.yaml"
    
    if not config_path.exists():
        logger.warning(f"Config file not found: {config_path}, using defaults")
        return {}
    
    try:
        with open(config_path, "r") as f:
            config = yaml.safe_load(f)
        return config.get("scrapers", {})
    except Exception as exc:
        logger.error(f"Failed to load scraper config: {exc}")
        return {}


async def run_scrape_job(job_id: UUID, request: ScrapeTriggerRequest) -> None:
    """
    Spustí scraping job pro vybrané zdroje.
    
    Args:
        job_id: UUID jobu
        request: ScrapeTriggerRequest s parametry
    """
    db_manager = get_db_manager()
    
    logger.info(f"Starting scrape job {job_id} with sources: {request.source_codes}, full_rescan: {request.full_rescan}")
    
    # 🔥 Load scraper configuration
    scraper_config = _load_scraper_config()
    
    try:
        # Update status na Running
        await db_manager.update_scrape_job(
            job_id=job_id,
            status="Running",
            started_at=datetime.utcnow(),
            progress=0
        )
        
        # Určit, které zdroje scrapovat
        source_codes: List[str] = request.source_codes or [
            "REMAX",
            "MMR",
            "PRODEJMETO",
            "ZNOJMOREALITY",
            "SREALITY",
            "IDNES",
        ]
        
        # Import scraperů až tady, aby byly lazy loaded
        from core.scrapers.remax_scraper import RemaxScraper
        from core.scrapers.mmreality_scraper import MmRealityScraper
        from core.scrapers.prodejmeto_scraper import ProdejmeToScraper
        from core.scrapers.sreality_scraper import SrealityScraper
        from core.scrapers.znojmoreality_scraper import ZnojmoRealityScraper
        from core.scrapers.idnes_reality_scraper import IdnesRealityScraper

        # Vybuduj tasku pro paralelní scraping
        tasks = []
        
        if "REMAX" in source_codes:
            logger.info(f"Job {job_id}: Scheduling REMAX scraper...")
            scraper = RemaxScraper()
            tasks.append(("REMAX", scraper.run(full_rescan=request.full_rescan)))

        if "MMR" in source_codes:
            logger.info(f"Job {job_id}: Scheduling MM Reality scraper...")
            # 🔥 Get MMReality config from settings.yaml
            mmreality_config = scraper_config.get("mmreality", {})
            search_configs = mmreality_config.get("search_configs")
            scraper = MmRealityScraper(search_configs=search_configs)
            tasks.append(("MMR", scraper.run(full_rescan=request.full_rescan)))

        if "PRODEJMETO" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Prodejme.to scraper...")
            scraper = ProdejmeToScraper()
            tasks.append(("PRODEJMETO", scraper.run(full_rescan=request.full_rescan)))

        if "ZNOJMOREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Znojmo Reality scraper...")
            scraper = ZnojmoRealityScraper()
            tasks.append(("ZNOJMOREALITY", scraper.run(full_rescan=request.full_rescan)))

        if "SREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Sreality scraper...")
            # 🔥 Get SREALITY config from settings.yaml
            sreality_config = scraper_config.get("sreality", {})
            detail_fetch_concurrency = sreality_config.get("detail_fetch_concurrency", 5)
            fetch_details = sreality_config.get("fetch_details", True)
            scraper = SrealityScraper(
                fetch_details=fetch_details,
                detail_fetch_concurrency=detail_fetch_concurrency
            )
            tasks.append(("SREALITY", scraper.run(full_rescan=request.full_rescan)))

        if "IDNES" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Idnes Reality scraper...")
            scraper = IdnesRealityScraper()
            tasks.append(("IDNES", scraper.run(full_rescan=request.full_rescan)))

        # Spusť všechny scrapers paralelně
        if tasks:
            source_names = [name for name, _ in tasks]
            coroutines = [coro for _, coro in tasks]
            
            # asyncio.gather spistí všechny tasks paralelně
            results = await asyncio.gather(*coroutines, return_exceptions=True)
            
            total_scraped = 0
            for (source_name, _), result in zip(tasks, results):
                if isinstance(result, Exception):
                    logger.error(f"Job {job_id}: {source_name} scraper failed: {result}")
                else:
                    total_scraped += result
                    logger.info(f"Job {job_id}: {source_name} scraped {result} listings")
            
            logger.info(f"Job {job_id}: All scrapers completed. Total listings: {total_scraped}")
            
            # Update status na Succeeded
            await db_manager.update_scrape_job(
                job_id=job_id,
                status="Succeeded",
                progress=100,
                finished_at=datetime.utcnow(),
                listings_found=total_scraped
            )
        else:
            logger.warning(f"Job {job_id}: No scrapers scheduled")
            await db_manager.update_scrape_job(
                job_id=job_id,
                status="Succeeded",
                progress=100,
                finished_at=datetime.utcnow(),
                error_message="No scrapers scheduled"
            )
        
    except Exception as exc:
        logger.exception(f"Job {job_id} failed with error: {exc}")
        await db_manager.update_scrape_job(
            job_id=job_id,
            status="Failed",
            error_message=str(exc),
            finished_at=datetime.utcnow()
        )
