"""
Job runner for scraping tasks.
Orchestrates individual scrapers and manages job lifecycle.
"""
import asyncio
import logging
import yaml
from pathlib import Path
from typing import Optional, List, Dict, Any, Awaitable
from uuid import UUID
from datetime import datetime

from api.schemas import ScrapeTriggerRequest
from core.database import get_db_manager
from core import notifications

logger = logging.getLogger(__name__)

# Max runtime per scraper task – prevents one hung source from blocking the whole job.
SCRAPER_TASK_TIMEOUT_SECONDS = 45 * 60


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
            "NEMZNOJMO",
            "HVREALITY",
            "PREMIAREALITY",
            "DELUXREALITY",
            "LEXAMO",
            "CENTURY21",
            "REAS",
            "BAZOS",
        ]
        
        # Import scraperů až tady, aby byly lazy loaded
        from core.scrapers.remax_scraper import RemaxScraper
        from core.scrapers.mmreality_scraper import MmRealityScraper
        from core.scrapers.prodejmeto_scraper import ProdejmeToScraper
        from core.scrapers.sreality_scraper import SrealityScraper
        from core.scrapers.znojmoreality_scraper import ZnojmoRealityScraper
        from core.scrapers.idnes_reality_scraper import IdnesRealityScraper
        from core.scrapers.nemovitostiznojmo_scraper import NemovitostiZnojmoScraper
        from core.scrapers.hvreality_scraper import HvRealityScraper
        from core.scrapers.premiareality_scraper import PremiaRealityScraper
        from core.scrapers.deluxreality_scraper import DeluxRealityScraper
        from core.scrapers.lexamo_scraper import LexamoScraper
        from core.scrapers.century21_scraper import Century21Scraper
        from core.scrapers.reas_scraper import ReasScraper
        from core.scrapers.bazos_scraper import BazosScraper

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
            locality_region_id = sreality_config.get("locality_region_id")
            max_pages_incremental = sreality_config.get("max_pages_incremental", 5)

            # Podpora více district IDs (locality_district_ids: [77, 79])
            # s fallbackem na starý skalární locality_district_id: 77
            district_ids: list = sreality_config.get("locality_district_ids") or []
            if not district_ids:
                single_id = sreality_config.get("locality_district_id")
                if single_id is not None:
                    district_ids = [single_id]

            # 🔥 Per-category scraping: každá kombinace district × category_main_cb
            # má vlastní scraper instanci. Bez toho by jeden query na celý okres
            # (všechny kategorie) vrátil 700+ výsledků a incremental (5 str. × 60 = 300)
            # by domy na stránkách 6+ vynechal.
            category_main_cbs: list = sreality_config.get("category_main_cbs") or [None]

            if district_ids:
                for district_id in district_ids:
                    for cat_main in category_main_cbs:
                        logger.info(
                            f"Job {job_id}: Scheduling Sreality scraper "
                            f"district_id={district_id} category_main_cb={cat_main}"
                        )
                        scraper = SrealityScraper(
                            category_main_cb=cat_main,
                            fetch_details=fetch_details,
                            detail_fetch_concurrency=detail_fetch_concurrency,
                            locality_region_id=locality_region_id,
                            locality_district_id=district_id,
                            max_pages_incremental=max_pages_incremental,
                        )
                        tasks.append(("SREALITY", scraper.run(full_rescan=request.full_rescan)))
            else:
                # Bez filtru okresu – celá republika (fallback)
                for cat_main in category_main_cbs:
                    scraper = SrealityScraper(
                        category_main_cb=cat_main,
                        fetch_details=fetch_details,
                        detail_fetch_concurrency=detail_fetch_concurrency,
                        locality_region_id=locality_region_id,
                        locality_district_id=None,
                        max_pages_incremental=max_pages_incremental,
                    )
                    tasks.append(("SREALITY", scraper.run(full_rescan=request.full_rescan)))

        if "IDNES" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Idnes Reality scraper...")
            scraper = IdnesRealityScraper()
            tasks.append(("IDNES", scraper.run(full_rescan=request.full_rescan)))

        if "NEMZNOJMO" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Nemovitosti Znojmo scraper...")
            scraper = NemovitostiZnojmoScraper()
            tasks.append(("NEMZNOJMO", scraper.run(full_rescan=request.full_rescan)))

        if "HVREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling HV Reality scraper...")
            scraper = HvRealityScraper()
            tasks.append(("HVREALITY", scraper.run(full_rescan=request.full_rescan)))

        if "PREMIAREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling PREMIA Reality scraper...")
            scraper = PremiaRealityScraper()
            tasks.append(("PREMIAREALITY", scraper.run(full_rescan=request.full_rescan)))

        if "DELUXREALITY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling DeluXreality scraper...")
            scraper = DeluxRealityScraper()
            tasks.append(("DELUXREALITY", scraper.run(full_rescan=request.full_rescan)))

        if "LEXAMO" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Lexamo scraper...")
            scraper = LexamoScraper()
            tasks.append(("LEXAMO", scraper.run(full_rescan=request.full_rescan)))

        if "CENTURY21" in source_codes:
            logger.info(f"Job {job_id}: Scheduling CENTURY 21 scraper...")
            scraper = Century21Scraper()
            tasks.append(("CENTURY21", scraper.run(full_rescan=request.full_rescan)))

        if "REAS" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Reas.cz scraper...")
            scraper = ReasScraper(fetch_details=True, detail_concurrency=5)
            tasks.append(("REAS", scraper.run(full_rescan=request.full_rescan)))

        if "BAZOS" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Bazos.cz scraper...")
            scraper = BazosScraper()
            tasks.append(("BAZOS", scraper.run(full_rescan=request.full_rescan)))

        # Čas před spuštěním scrapingu – slouží pro deaktivaci neviděných inzerátů
        scrape_started_at = datetime.utcnow()

        # Spusť všechny scrapers paralelně (každý s timeoutem)
        if tasks:
            source_names = [name for name, _ in tasks]

            async def _run_with_timeout(name: str, coro: Awaitable[int]) -> int:
                try:
                    return await asyncio.wait_for(coro, timeout=SCRAPER_TASK_TIMEOUT_SECONDS)
                except asyncio.TimeoutError:
                    logger.error(
                        "Job %s: %s scraper timed out after %ss",
                        job_id,
                        name,
                        SCRAPER_TASK_TIMEOUT_SECONDS,
                    )
                    raise

            coroutines = [_run_with_timeout(name, coro) for name, coro in tasks]

            # asyncio.gather spustí všechny tasks paralelně; chyba jednoho nezastaví ostatní
            results = await asyncio.gather(*coroutines, return_exceptions=True)
            
            total_scraped = 0
            for (source_name, _), result in zip(tasks, results):
                if isinstance(result, Exception):
                    logger.error(f"Job {job_id}: {source_name} scraper failed: {result}")
                else:
                    total_scraped += result
                    logger.info(f"Job {job_id}: {source_name} scraped {result} listings")
                    # Po úspěšném full_rescan deaktivuj inzeráty které scraper neviděl
                    # ⚠️ OCHRANA: deaktivuj POUZE pokud scraper vrátil alespoň 1 inzerát.
                    # Pokud vrátí 0 (síťová chyba, timeout), NESMÍME deaktivovat stávající
                    # inzeráty – způsobilo by to falešnou masovou deaktivaci celé DB.
                    if request.full_rescan and result > 0:
                        deactivated = await db_manager.deactivate_unseen_listings(source_name, scrape_started_at)
                        if deactivated > 0:
                            logger.info(f"Job {job_id}: {source_name} deactivated {deactivated} expired listings")
                    elif request.full_rescan and result == 0:
                        logger.warning(f"Job {job_id}: {source_name} returned 0 listings during full_rescan – skipping deactivation to prevent false mass-deactivation")

            logger.info(f"Job {job_id}: All scrapers completed. Total listings: {total_scraped}")

            # Slack notifikace – pošle jen pokud něco selhalo nebo vrátilo 0
            job_results = {name: res for (name, _), res in zip(tasks, results)}
            await notifications.notify_job_summary(
                job_id=str(job_id),
                results=job_results,
                full_rescan=request.full_rescan,
            )

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
