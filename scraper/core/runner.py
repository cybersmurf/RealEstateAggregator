"""
Job runner for scraping tasks.
Orchestrates individual scrapers and manages job lifecycle.
"""
import asyncio
import logging
import os

import httpx
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
            "REALINGO",
            "REALMIX",
            "REALITYCECHY",
            "REALITYMIX",
            "REALCITY",
            "BEZREALITKY",
            "OKDRAZBY",
            "ULOVDOMOV",
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
        from core.scrapers.realingo_scraper import RealingoScraper
        from core.scrapers.realmix_scraper import RealmixScraper
        from core.scrapers.realitycechy_scraper import RealityCechyScraper
        from core.scrapers.realitymix_scraper import RealityMixScraper
        from core.scrapers.realcity_scraper import RealcityScraper
        from core.scrapers.bezrealitky_scraper import BezrealitkyScraper
        from core.scrapers.okdrazby_scraper import OkdrazbyScraper
        from core.scrapers.ulovdomov_scraper import UlovDomovScraper

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
            # Typ nabídky: 1=Prodej, 2=Pronájem, 3=Dražba. Pronájem pozemků a „ostatního" se nebere
            # (filtry je stejně nepouštějí), dražby jen u bytů, domů a pozemků – jinak by se pro každý
            # okres spouštěly prázdné dotazy. Do 6. 10. 2026 chyběl pronájem komerčních prostor
            # a dražby bytů, přestože je search_filters povolují.
            category_type_cbs: list = sreality_config.get("category_type_cbs") or [1]

            def _type_applies(cat_main, cat_type) -> bool:
                if cat_type == 2:
                    return cat_main in (1, 2, 4, None)
                if cat_type == 3:
                    return cat_main in (1, 2, 3, None)
                return True

            if district_ids:
                for district_id in district_ids:
                    for cat_main in category_main_cbs:
                        for cat_type in category_type_cbs:
                            if not _type_applies(cat_main, cat_type):
                                continue
                            logger.info(
                                f"Job {job_id}: Scheduling Sreality scraper "
                                f"district_id={district_id} category_main_cb={cat_main} category_type_cb={cat_type}"
                            )
                            scraper = SrealityScraper(
                                category_main_cb=cat_main,
                                category_type_cb=cat_type,
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

        if "REALINGO" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Realingo scraper...")
            scraper = RealingoScraper()
            tasks.append(("REALINGO", scraper.run(full_rescan=request.full_rescan)))

        if "REALMIX" in source_codes:
            logger.info(f"Job {job_id}: Scheduling REALmix (reality-znojmo.cz) scraper...")
            scraper = RealmixScraper()
            tasks.append(("REALMIX", scraper.run(full_rescan=request.full_rescan)))

        if "REALITYCECHY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Reality Čechy scraper...")
            scraper = RealityCechyScraper()
            tasks.append(("REALITYCECHY", scraper.run(full_rescan=request.full_rescan)))

        if "REALITYMIX" in source_codes:
            logger.info(f"Job {job_id}: Scheduling RealityMIX scraper...")
            scraper = RealityMixScraper()
            tasks.append(("REALITYMIX", scraper.run(full_rescan=request.full_rescan)))

        if "REALCITY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Realcity scraper...")
            scraper = RealcityScraper()
            tasks.append(("REALCITY", scraper.run(full_rescan=request.full_rescan)))

        if "BEZREALITKY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling Bezrealitky scraper...")
            scraper = BezrealitkyScraper()
            tasks.append(("BEZREALITKY", scraper.run(full_rescan=request.full_rescan)))

        if "OKDRAZBY" in source_codes:
            logger.info(f"Job {job_id}: Scheduling OK dražby scraper...")
            scraper = OkdrazbyScraper()
            tasks.append(("OKDRAZBY", scraper.run(full_rescan=request.full_rescan)))

        if "ULOVDOMOV" in source_codes:
            logger.info(f"Job {job_id}: Scheduling UlovDomov scraper...")
            scraper = UlovDomovScraper()
            tasks.append(("ULOVDOMOV", scraper.run(full_rescan=request.full_rescan)))

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
                        # ⚠️ OCHRANA 2: částečně rozbitý parser (vrátí 2 ze 100) by prošel
                        # podmínkou result > 0 a deaktivoval zbytek. Proto nedeaktivuj víc
                        # inzerátů, než kolik jich scraper v tomhle běhu viděl (result počítá
                        # i inzeráty vyřazené filtrem). Minimum 10, ať malé zdroje fungují.
                        unseen = await db_manager.count_unseen_listings(source_name, scrape_started_at)
                        if unseen > max(result, 10):
                            logger.warning(
                                f"Job {job_id}: {source_name} saw only {result} listings but {unseen} "
                                f"would be deactivated – skipping deactivation (likely broken parser)"
                            )
                        else:
                            deactivated = await db_manager.deactivate_unseen_listings(source_name, scrape_started_at)
                            if deactivated > 0:
                                logger.info(f"Job {job_id}: {source_name} deactivated {deactivated} expired listings")
                    elif request.full_rescan and result == 0:
                        logger.warning(f"Job {job_id}: {source_name} returned 0 listings during full_rescan – skipping deactivation to prevent false mass-deactivation")

            logger.info(f"Job {job_id}: All scrapers completed. Total listings: {total_scraped}")

            # Historie běhů podle zdroje: propad proti minulým běhům je jediný signál, že se web
            # změnil a parser vrací zlomek (ne nulu, ne výjimku). Historie se čte před zápisem tohoto běhu.
            drops: Dict[str, tuple] = {}
            anomalies: Dict[str, int] = {}
            try:
                history = await db_manager.recent_run_counts(request.full_rescan)
                current = {name: res for (name, _), res in zip(tasks, results) if isinstance(res, int)}
                drops = notifications.detect_drops(current, history)
                finished_at = datetime.utcnow()
                for (source_name, _), result in zip(tasks, results):
                    failed = isinstance(result, Exception)
                    await db_manager.record_scrape_run(
                        source_name, scrape_started_at, finished_at,
                        "Failed" if failed else "Succeeded",
                        0 if failed else int(result), request.full_rescan,
                        f"{type(result).__name__}: {result}" if failed else None,
                    )
                anomalies = await db_manager.count_data_anomalies(scrape_started_at)
                if drops:
                    logger.warning(f"Job {job_id}: Result drop vs. previous runs: {drops}")
                if anomalies:
                    logger.warning(f"Job {job_id}: Suspicious values in new listings: {anomalies}")
            except Exception as exc:  # noqa: BLE001 – hlídání je best-effort, job už uspěl
                logger.warning(f"Job {job_id}: Run history / drift check failed: {exc}")

            # Okres u inzerátů, kterým ho zdroj nedal (Reas, Prodejme.to, iDNES) – z GPS, jinak z obce.
            # Před detekcí duplikátů: ta okresy porovnává a hledání podle okresu je jinak nenajde.
            try:
                from_gps, from_names = await db_manager.fill_missing_districts()
                if from_gps or from_names:
                    logger.info(f"Job {job_id}: District filled – {from_gps} z GPS, {from_names} z názvu obce")
            except Exception as exc:  # noqa: BLE001 – doplnění je best-effort, job už uspěl
                logger.warning(f"Job {job_id}: District fill failed: {exc}")

            # GPS pro inzeráty bez souřadnic (iDNES, Realingo, REALmix, Bazoš, RE/MAX…): Nominatim,
            # 1 dotaz/s, proto jen dávka za běh. Dřív se geokódovalo jen ručně přes /v1/geocode/bulk
            # a 4 600 aktivních inzerátů bylo bez polohy (audit 7. 10. 2026). Před duplicitami – ty GPS porovnávají.
            try:
                from core.geocoding import bulk_geocode
                geocode_batch = int(os.getenv("GEOCODE_BATCH_PER_RUN", "150"))
                if geocode_batch > 0:
                    geocoded = await bulk_geocode(db_manager, batch_size=geocode_batch)
                    logger.info(f"Job {job_id}: Geocoded {geocoded} listings (batch {geocode_batch})")
            except Exception as exc:  # noqa: BLE001 – geokódování je best-effort, job už uspěl
                logger.warning(f"Job {job_id}: Geocoding failed: {exc}")

            # Přepočítej cross-source duplikáty (stejný dům na SREALITY + BAZOS + …).
            # Detekci vlastní .NET API; selhání nesmí shodit scrape job.
            await _trigger_duplicate_detection(job_id)
            await _trigger_gallery_reclassification(job_id, db_manager.pop_galleries_to_reclassify())
            await _trigger_dead_listing_check(job_id)

            # Uložená hledání uživatelů – nové inzeráty a zlevnění (e-mail / Telegram)
            await _trigger_saved_search_notifications(job_id)

            # Slack notifikace – pošle jen pokud něco selhalo nebo vrátilo 0
            job_results = {name: res for (name, _), res in zip(tasks, results)}
            await notifications.notify_job_summary(
                job_id=str(job_id),
                results=job_results,
                full_rescan=request.full_rescan,
                drops=drops,
                anomalies=anomalies,
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


def _api_key_headers() -> Dict[str, str]:
    """Hlavní API klíč pro volání správcovských endpointů .NET API po scrapu."""
    api_key = os.environ.get("API_KEY")
    return {"X-Api-Key": api_key} if api_key else {}


async def _trigger_saved_search_notifications(job_id: UUID) -> None:
    """Po scrapu nechá API vyhodnotit uložená hledání (nové inzeráty, zlevnění) a rozeslat upozornění."""
    api_base_url = os.environ.get("API_BASE_URL", "http://realestate-api:8080")
    url = f"{api_base_url.rstrip('/')}/api/saved-searches/run"
    try:
        async with httpx.AsyncClient(timeout=300) as client:
            resp = await client.post(url, headers=_api_key_headers())
            resp.raise_for_status()
            data = resp.json()
            logger.info(
                "Job %s: Saved searches – %s hledání, %s nových, %s zlevnění, %s doručeno",
                job_id,
                data.get("searchesEvaluated"),
                data.get("newListings"),
                data.get("priceDrops"),
                data.get("notificationsSent"),
            )
    except Exception as exc:  # noqa: BLE001 – upozornění jsou best-effort, job už uspěl
        logger.warning("Job %s: Saved search notification call failed: %s", job_id, exc)


async def _trigger_dead_listing_check(job_id: UUID) -> None:
    """Po scrapu nechá API ověřit HEADem inzeráty, které žádný běh 2 dny neviděl.

    Inkrementální běh projde jen prvních N stránek; dům stažený ze Sreality tak
    zůstal aktivní s mrtvým odkazem až do plného rescanu. HEAD 404/410 → deaktivace,
    cokoli jiného (301, 403, timeout) nechá inzerát být.
    """
    api_base_url = os.environ.get("API_BASE_URL", "http://realestate-api:8080")
    url = f"{api_base_url.rstrip('/')}/api/listings/deactivate-dead?daysOld=2"
    headers = _api_key_headers()
    try:
        async with httpx.AsyncClient(timeout=1800) as client:
            resp = await client.post(url, headers=headers)
            resp.raise_for_status()
            data = resp.json()
            logger.info(
                "Job %s: Dead listing check – %s zkontrolováno, %s deaktivováno",
                job_id,
                data.get("checked", data.get("Checked")),
                data.get("deactivated", data.get("Deactivated")),
            )
    except Exception as exc:  # noqa: BLE001 – kontrola je best-effort, job už uspěl
        logger.warning("Job %s: Dead listing check call failed: %s", job_id, exc)


async def _trigger_gallery_reclassification(job_id: UUID, listing_ids: List[UUID]) -> None:
    """Po scrapu nechá API doklasifikovat galerie, do kterých zdroj poslal nové fotky.

    Scraper klasifikaci na novou URL nepřenáší (makléř fotky nahrál znovu, často v jiném pořadí),
    takže dřív klasifikovaný inzerát by zůstal zčásti nebo úplně bez kategorií. Volá se až po
    detekci duplicit – API klasifikuje fotky toho člena skupiny, jehož galerie se zobrazuje.
    """
    if not listing_ids:
        return
    api_base_url = os.environ.get("API_BASE_URL", "http://realestate-api:8080")
    url = f"{api_base_url.rstrip('/')}/api/photos/bulk-classify"
    queued = 0
    try:
        async with httpx.AsyncClient(timeout=60) as client:
            for listing_id in listing_ids:
                resp = await client.post(
                    url, params={"listingId": str(listing_id), "wait": "false"}, headers=_api_key_headers()
                )
                resp.raise_for_status()
                queued += 1
    except Exception as exc:  # noqa: BLE001 – doklasifikování je best-effort, job už uspěl
        logger.warning("Job %s: Gallery reclassification call failed: %s", job_id, exc)
    logger.info("Job %s: Gallery reclassification – %s z %s galerií zařazeno", job_id, queued, len(listing_ids))


async def _trigger_duplicate_detection(job_id: UUID) -> None:
    """Po scrapu požádá API o přepočet duplicate_of_listing_id.

    Bez tohoto kroku by nové inzeráty zůstaly neoznačené a stejný dům by se
    v aplikaci zobrazoval vícekrát s protichůdnými AI cenovými signály.
    """
    api_base_url = os.environ.get("API_BASE_URL", "http://realestate-api:8080")
    url = f"{api_base_url.rstrip('/')}/api/listings/detect-duplicates"
    # Endpoint je od zavedení účtů jen pro správce – hlavní API klíč (API_KEY) ho identifikuje.
    headers = _api_key_headers()
    try:
        async with httpx.AsyncClient(timeout=120) as client:
            resp = await client.post(url, headers=headers)
            resp.raise_for_status()
            data = resp.json()
            logger.info(
                "Job %s: Duplicate detection – %s aktivních, %s skupin, %s duplikátů",
                job_id,
                data.get("activeListings"),
                data.get("clusters"),
                data.get("duplicatesMarked"),
            )
    except Exception as exc:  # noqa: BLE001 – detekce je best-effort, job už uspěl
        logger.warning("Job %s: Duplicate detection call failed: %s", job_id, exc)
