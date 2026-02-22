"""
Job runner for scraping tasks.
Orchestrates individual scrapers and manages job lifecycle.
"""
import logging
from typing import Optional, List, Dict
from uuid import UUID
from datetime import datetime

from api.schemas import ScrapeTriggerRequest, ScrapeJob

logger = logging.getLogger(__name__)


async def run_scrape_job(
    job_id: UUID,
    request: ScrapeTriggerRequest,
    jobs_store: Dict[str, ScrapeJob]
) -> None:
    """
    Spustí scraping job pro vybrané zdroje.
    
    Args:
        job_id: UUID jobu
        request: ScrapeTriggerRequest s parametry
        jobs_store: Reference na dictionary se všemi joby (pro update statusu)
    """
    job_key = str(job_id)
    job: Optional[ScrapeJob] = jobs_store.get(job_key)
    if job is None:
        logger.error(f"Job {job_id} not found in store")
        return

    # Update status na Started
    job.status = "Started"
    jobs_store[job_key] = job
    
    logger.info(f"Starting scrape job {job_id} with sources: {request.source_codes}, full_rescan: {request.full_rescan}")

    try:
        # Určit, které zdroje scrapovat
        source_codes: List[str] = request.source_codes or [
            "REMAX",
            "MMR",
            "PRODEJMETO",
            "ZNOJMOREALITY",
            "SREALITY",
        ]
        
        # Import scraperů až tady, aby byly lazy loaded
        from core.scrapers.remax_scraper import RemaxScraper
        from core.scrapers.mmreality_scraper import MmRealityScraper
        from core.scrapers.prodejmeto_scraper import ProdejmeToScraper
        from core.scrapers.sreality_scraper import SrealityScraper
        from core.scrapers.znojmoreality_scraper import ZnojmoRealityScraper

        scraped_count = 0

        if "REMAX" in source_codes:
            logger.info(f"Job {job_id}: Scraping REMAX...")
            scraper = RemaxScraper()
            count = await scraper.run(full_rescan=request.full_rescan)
            scraped_count += count
            logger.info(f"Job {job_id}: REMAX scraped {count} listings")

        if "MMR" in source_codes:
            logger.info(f"Job {job_id}: Scraping MM Reality...")
            scraper = MmRealityScraper()
            count = await scraper.run(full_rescan=request.full_rescan)
            scraped_count += count
            logger.info(f"Job {job_id}: MMR scraped {count} listings")

        if "PRODEJMETO" in source_codes:
            logger.info(f"Job {job_id}: Scraping Prodejme.to...")
            scraper = ProdejmeToScraper()
            count = await scraper.run(full_rescan=request.full_rescan)
            scraped_count += count
            logger.info(f"Job {job_id}: Prodejme.to scraped {count} listings")

        if "ZNOJMOREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scraping Znojmo Reality...")
            scraper = ZnojmoRealityScraper()
            count = await scraper.run(full_rescan=request.full_rescan)
            scraped_count += count
            logger.info(f"Job {job_id}: Znojmo Reality scraped {count} listings")

        if "SREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scraping Sreality...")
            scraper = SrealityScraper()
            count = await scraper.run(full_rescan=request.full_rescan)
            scraped_count += count
            logger.info(f"Job {job_id}: Sreality scraped {count} listings")

        # Success
        job.status = "Succeeded"
        logger.info(f"Job {job_id} completed successfully. Total scraped: {scraped_count}")
        
    except Exception as exc:
        logger.exception(f"Job {job_id} failed with error: {exc}")
        job.status = "Failed"
        job.error_message = str(exc)
        
    finally:
        jobs_store[job_key] = job
