"""
Jednorázové dotažení detailů všech aktivních inzerátů Bazoše (7. 11. 2026).

Do 6. 11. 2026 scraper určoval typ z prvního odkazu stránky (levé menu „/prodam/byt/"), takže
zahrady, pozemky i domy z neznámé rubriky byly „byt", a pronájem hledal jako „/pronajem/"
(Bazoš píše „/pronajmu/"), takže pronájmy byly prodej. Dvoufázový běh stahuje detail jen u nových
inzerátů a při změně ceny – staré řádky by se samy neopravily. Skript stáhne detail každého
aktivního inzerátu znovu a uloží ho opraveným parserem; smazaný inzerát (Bazoš přesměruje na
výpis /inzeraty/…) rovnou deaktivuje.

Spuštění v kontejneru scraperu:
    docker exec -d realestate-scraper python scripts/refresh_bazos_details.py
    docker exec realestate-scraper tail -f /tmp/refresh_bazos_details.log
Volitelně LIMIT=50.
"""
import asyncio
import logging
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import httpx

from core.database import init_db_manager
from core.scrapers.bazos_scraper import DEFAULT_HEADERS, BazosScraper

LOG_PATH = os.getenv("LOG_PATH", "/tmp/refresh_bazos_details.log")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s",
                    handlers=[logging.FileHandler(LOG_PATH), logging.StreamHandler(sys.stdout)])
logger = logging.getLogger("refresh_bazos_details")


async def main() -> int:
    db = init_db_manager(
        host=os.getenv("DB_HOST", "localhost"), port=int(os.getenv("DB_PORT", "5432")),
        database=os.getenv("DB_NAME", "realestate_dev"), user=os.getenv("DB_USER", "postgres"),
        password=os.getenv("DB_PASSWORD", "dev"), min_size=1, max_size=3)
    await db.connect()
    async with db.acquire() as conn:
        rows = await conn.fetch("""
            SELECT external_id, url, title, district, property_type, offer_type
            FROM re_realestate.listings WHERE source_code = 'BAZOS' AND is_active
            ORDER BY (property_type = 'Apartment') DESC, first_seen_at DESC""")
    limit = int(os.getenv("LIMIT", "0") or 0)
    if limit:
        rows = rows[:limit]
    logger.info("Bazoš: %s aktivních inzerátů k obnově", len(rows))

    scraper = BazosScraper()
    changed = gone = failed = 0
    async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
        scraper._http_client = client
        for i, r in enumerate(rows, 1):
            try:
                resp = None
                for wait in (30, 60, 120, None):
                    resp = await client.get(r["url"])
                    if resp.status_code != 429 or wait is None:
                        break
                    logger.warning("429 – čekám %s s", wait)
                    await asyncio.sleep(wait)
                if resp.status_code in (404, 410) or "/inzerat/" not in str(resp.url):
                    await db.deactivate_listing("BAZOS", r["external_id"])
                    gone += 1
                    continue
                resp.raise_for_status()
                item = {"external_id": r["external_id"], "detail_url": r["url"], "title": r["title"],
                        "district": r["district"]}
                listing = scraper._parse_detail_page(resp.text, item)
                if listing is None:
                    continue
                if r["district"]:
                    listing["district"] = r["district"]
                await db.upsert_listing(listing)
                types = {"Byt": "Apartment", "Dům": "House", "Pozemek": "Land", "Chata": "Cottage",
                         "Garáž": "Garage", "Komerční": "Commercial"}
                offers = {"Prodej": "Sale", "Pronájem": "Rent"}
                if (types.get(listing["property_type"], "Other"), offers.get(listing["offer_type"])) != (
                        r["property_type"], r["offer_type"]):
                    changed += 1
            except Exception as exc:  # noqa: BLE001
                failed += 1
                logger.error("%s: %r", r["url"], exc)
            if i % 100 == 0:
                logger.info("%s/%s – změněný typ %s, smazané %s, chyby %s", i, len(rows), changed, gone, failed)
            await asyncio.sleep(1.2)
    logger.info("Hotovo: %s inzerátů, změněný typ/nabídka %s, smazané %s, chyby %s", len(rows), changed, gone, failed)
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
