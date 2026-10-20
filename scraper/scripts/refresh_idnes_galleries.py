"""
Jednorázová obnova galerií iDNES (20. 10. 2026).

Do té doby scraper bral všechny <img> ze stránky detailu – placeholder, špendlík mapy, 130×80 náhledy,
avatar makléře, logo kanceláře a fotky cizích domů z „Podobných nabídek“. Skript znovu stáhne detail
každého aktivního iDNES inzerátu, vytáhne galerii novým parserem (IdnesRealityScraper.extract_gallery_photos)
a projede _upsert_photos: cizí a placeholder řádky zmizí, nové URL velkých fotek se vloží neklasifikované.
Klasifikaci neobjednává – o tom rozhoduje Petr (kredit obrazových modelů).

Spuštění v kontejneru scraperu:
    docker exec -d realestate-scraper python scripts/refresh_idnes_galleries.py
    docker exec realestate-scraper tail -f /tmp/refresh_idnes_galleries.log
Volitelně: LIMIT=50 (jen prvních N), LISTING_ID=<uuid> (jen jeden), CONCURRENCY=3.
"""
import asyncio
import logging
import os
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import httpx
from bs4 import BeautifulSoup

from core.database import init_db_manager
from core.scrapers.idnes_reality_scraper import IdnesRealityScraper

LOG_PATH = os.getenv("LOG_PATH", "/tmp/refresh_idnes_galleries.log")
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    handlers=[logging.FileHandler(LOG_PATH), logging.StreamHandler(sys.stdout)],
)
logger = logging.getLogger("refresh_idnes_galleries")

USER_AGENT = (
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/129.0 Safari/537.36"
)


async def main() -> int:
    db = init_db_manager(
        host=os.getenv("DB_HOST", "localhost"),
        port=int(os.getenv("DB_PORT", "5432")),
        database=os.getenv("DB_NAME", "realestate_dev"),
        user=os.getenv("DB_USER", "postgres"),
        password=os.getenv("DB_PASSWORD", "dev"),
        min_size=1,
        max_size=4,
    )
    await db.connect()

    where = "l.source_code = 'IDNES' AND l.is_active"
    params: List[Any] = []
    if os.getenv("LISTING_ID"):
        where += " AND l.id = $1::uuid"
        params.append(os.environ["LISTING_ID"])
    limit = int(os.getenv("LIMIT", "0"))

    async with db._pool.acquire() as conn:
        rows = await conn.fetch(
            f"SELECT l.id, l.url, (SELECT count(*) FROM re_realestate.listing_photos p WHERE p.listing_id = l.id) AS photos "
            f"FROM re_realestate.listings l WHERE {where} ORDER BY l.first_seen_at DESC"
            + (f" LIMIT {limit}" if limit > 0 else ""),
            *params,
        )
    logger.info("iDNES galerie k obnově: %d inzerátů", len(rows))

    stats: Dict[str, int] = {"done": 0, "changed": 0, "same": 0, "empty": 0, "gone": 0, "failed": 0}
    before_total = sum(r["photos"] for r in rows)
    after_total = 0
    semaphore = asyncio.Semaphore(int(os.getenv("CONCURRENCY", "3")))
    started = time.monotonic()

    async with httpx.AsyncClient(timeout=60, follow_redirects=True, headers={"User-Agent": USER_AGENT}) as client:

        async def refresh(row: Any) -> None:
            nonlocal after_total
            listing_id, url, old_count = row["id"], row["url"], row["photos"]
            async with semaphore:
                try:
                    response = await client.get(url)
                except (httpx.TimeoutException, httpx.TransportError) as exc:
                    stats["failed"] += 1
                    after_total += old_count
                    logger.warning("%s: fetch selhal (%s)", url, exc)
                    return
                if response.status_code in (404, 410):
                    stats["gone"] += 1
                    after_total += old_count
                    logger.info("%s: %d – inzerát zmizel, nechávám noční deaktivaci", url, response.status_code)
                    return
                if response.status_code != 200:
                    stats["failed"] += 1
                    after_total += old_count
                    logger.warning("%s: HTTP %d", url, response.status_code)
                    return

                photos = IdnesRealityScraper.extract_gallery_photos(BeautifulSoup(response.text, "html.parser"))
                if not photos:
                    stats["empty"] += 1
                    after_total += old_count
                    logger.warning("%s: galerie prázdná, fotky nechávám", url)
                    return

                async with db._pool.acquire() as conn:
                    await db._upsert_photos(conn, listing_id, photos)
                    new_count = await conn.fetchval(
                        "SELECT count(*) FROM re_realestate.listing_photos WHERE listing_id = $1", listing_id
                    )
                after_total += new_count
                stats["changed" if new_count != old_count else "same"] += 1
                logger.info("%s: %d → %d fotek", url, old_count, new_count)

        async def worker(row: Any) -> None:
            await refresh(row)
            stats["done"] += 1
            if stats["done"] % 100 == 0:
                elapsed = time.monotonic() - started
                logger.info("Průběh: %d/%d, %.0f s, %s", stats["done"], len(rows), elapsed, stats)
            await asyncio.sleep(0.3)

        await asyncio.gather(*(worker(r) for r in rows))

    db.pop_galleries_to_reclassify()  # klasifikaci neobjednáváme
    await db.disconnect()
    logger.info(
        "HOTOVO za %.0f s: %s; fotek celkem %d → %d", time.monotonic() - started, stats, before_total, after_total
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
