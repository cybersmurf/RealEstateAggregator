"""
Reas.cz scraper (www.reas.cz).

Site characteristics:
- Next.js SSR aplikace → kompletní data inzerátu jsou v __NEXT_DATA__ JSON
  přímo v HTML každé listingové stránky. Není potřeba Playwright ani JS rendering.
- Stránkování: ?page=N, limit=10 inzerátů na stránce
- Kategorie: prodej/{byty,domy,pozemky,komerci,ostatni}
             pronajem/{byty,domy,komerci,ostatni}
- GPS souřadnice dostupné přímo v SSR datech (point.coordinates = [lng, lat])
- Fotky: imagesWithMetadata[].original (Google Cloud Storage)
- external_id: MongoDB _id pole

Detail stránky jsou fetchovány pro získání popisu (popis není v listu).
"""
import asyncio
import logging
import math
import re
from typing import Any, Dict, List, Optional, Tuple
import json as json_module

import httpx
from bs4 import BeautifulSoup

from ..http_utils import http_retry
from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.reas.cz"

# (url_cesta, offer_type, segment_key, locality_hint)
# lokality: 'jihomoravsky-kraj/cena-do-10-milionu' = JMK, prodej do 10M Kč
# locality_hint se připojí k location_text aby prošel geo filtrem (subobce např. Oblekovice neobsahují 'znojmo')
#   → "jihomoravsk" je v target_districts, takže "Jihomoravský kraj" filtrem projde
#
# POZOR: Kategorie pozemky/komerci s lokálním filtrem vracejí count=5124 (= celá ČR).
# Lokální filtr pro tyto segmenty na reas.cz nefunguje → byly odebrány.
# Fungující segmenty s jihomoravsky-kraj/cena-do-10-milionu:
#   domy (count~141, ~15 stran) – byty vynechány (apartments: enabled: false v settings.yaml)
CATEGORIES: List[Tuple[str, str, str, str]] = [
    ("prodej/domy/jihomoravsky-kraj/cena-do-10-milionu", "Sale", "domy", "Jihomoravský kraj"),
]

# Mapování type/subType z reas.cz → naše DB hodnoty
PROPERTY_TYPE_MAP: Dict[str, str] = {
    "flat": "Apartment",
    "house": "House",
    "land": "Land",
    "commercial": "Commercial",
    "cottage": "Cottage",
    "garage": "Garage",
    "other": "Other",
}

# Segmenty URL k českému názvu pro title
SEGMENT_NAMES: Dict[str, str] = {
    "byty": "bytu",
    "domy": "domu",
    "pozemky": "pozemku",
    "komerci": "komerční nemovitosti",
    "ostatni": "nemovitosti",
}

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9,en;q=0.8",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}

PAGE_LIMIT = 10  # reas.cz vrací 10 inzerátů na stránku


class ReasScraper:
    """Scraper pro reas.cz – číst data z Next.js SSR __NEXT_DATA__."""

    SOURCE_CODE = "REAS"

    def __init__(self, fetch_details: bool = True, detail_concurrency: int = 5):
        """
        Args:
            fetch_details: Fetchovat detail stránky pro popis. True = plná data.
            detail_concurrency: Počet paralelních detail požadavků.
        """
        self.fetch_details = fetch_details
        self.detail_concurrency = detail_concurrency
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """Vstupní bod pro runner."""
        return await self.scrape(full_rescan=full_rescan)

    async def scrape(self, full_rescan: bool = False) -> int:
        logger.info("Starting Reas.cz scraper (full_rescan=%s)", full_rescan)
        total = 0

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                for category_path, offer_type, segment_key, locality_hint in CATEGORIES:
                    try:
                        count = await self._scrape_category(
                            category_path, offer_type, segment_key, locality_hint, full_rescan, metrics
                        )
                        total += count
                        logger.info(
                            "Reas.cz [%s] done – %s listings", category_path, count
                        )
                        await asyncio.sleep(1.0)
                    except Exception as exc:
                        logger.error(
                            "Reas.cz [%s] failed: %s", category_path, exc
                        )
                        metrics.increment_failed()

                self._http_client = None

        logger.info("Reas.cz scraper finished. Total scraped: %s", total)
        return total

    async def _scrape_category(
        self,
        category_path: str,
        offer_type: str,
        segment_key: str,
        locality_hint: str,
        full_rescan: bool,
        metrics: Any,
    ) -> int:
        """Projde všechny stránky dané kategorie a uloží inzeráty."""
        segment = segment_key  # byty / domy / pozemky …

        # Zjisti počet stránek z první stránky
        first_page_data = await self._fetch_listing_page(category_path, 1)
        if not first_page_data:
            logger.warning("Reas.cz [%s]: no data on page 1", category_path)
            return 0

        total_count = first_page_data.get("count", 0)
        total_pages = max(1, math.ceil(total_count / PAGE_LIMIT))
        logger.info(
            "Reas.cz [%s]: total=%s listings on %s pages",
            category_path, total_count, total_pages,
        )

        # Bezpečnostní guard: pokud count > 500, lokalitní filtr zřejmě nefunguje
        # a URL vrací celonárodní data. Přeskočit kategorii.
        MAX_EXPECTED_CATEGORY_COUNT = 500
        if total_count > MAX_EXPECTED_CATEGORY_COUNT:
            logger.error(
                "Reas.cz [%s]: count=%s > %s – lokalitní filtr nefunguje! "
                "Kategorie přeskočena aby nedošlo ke stahování celonárodních dat.",
                category_path, total_count, MAX_EXPECTED_CATEGORY_COUNT,
            )
            return 0

        # Pokud ne full_rescan, omezte na 2 stránky (posledních ~20 inzerátů = novinky)
        max_pages = total_pages if full_rescan else min(2, total_pages)

        scraped = 0
        for page_num in range(1, max_pages + 1):
            if page_num == 1:
                page_data = first_page_data
            else:
                page_data = await self._fetch_listing_page(category_path, page_num)
                await asyncio.sleep(0.5)

            if not page_data:
                continue

            ads = page_data.get("data", [])

            if self.fetch_details:
                # Fetch detailů paralelně (po dávkách)
                sem = asyncio.Semaphore(self.detail_concurrency)
                tasks = [
                    self._process_ad_with_detail(ad, offer_type, segment, locality_hint, sem, metrics)
                    for ad in ads
                ]
                results = await asyncio.gather(*tasks, return_exceptions=True)
                for r in results:
                    if isinstance(r, Exception):
                        logger.error("Reas.cz detail fetch error: %s", r)
                    elif r:
                        scraped += 1
            else:
                for ad in ads:
                    try:
                        listing = self._build_listing(ad, offer_type, segment, description=None, locality_hint=locality_hint)
                        await self._save_listing(listing)
                        scraped += 1
                        metrics.increment_scraped()
                    except Exception as exc:
                        logger.error("Reas.cz ad %s error: %s", ad.get("_id"), exc)
                        metrics.increment_failed()

        return scraped

    async def _process_ad_with_detail(
        self,
        ad: Dict[str, Any],
        offer_type: str,
        segment: str,
        locality_hint: str,
        sem: asyncio.Semaphore,
        metrics: Any,
    ) -> bool:
        """Fetchne detail stránky, sestaví listing a uloží."""
        async with sem:
            detail_url = ad.get("link", "")
            description = None
            if detail_url:
                try:
                    html = await self._fetch_html(detail_url)
                    description = self._parse_description(html)
                    await asyncio.sleep(0.3)
                except Exception as exc:
                    logger.debug("Reas.cz detail %s: %s", detail_url, exc)

            try:
                listing = self._build_listing(ad, offer_type, segment, description, locality_hint=locality_hint)
                await self._save_listing(listing)
                metrics.increment_scraped()
                return True
            except Exception as exc:
                logger.error("Reas.cz save %s error: %s", ad.get("_id"), exc)
                metrics.increment_failed()
                return False

    # ─── HTTP helpers ────────────────────────────────────────────────────────

    @http_retry
    async def _fetch_listing_page(
        self, category_path: str, page: int
    ) -> Optional[Dict[str, Any]]:
        """Stáhne HTML stránku kategorie a extrahuje adsListResult z __NEXT_DATA__."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        url = f"{BASE_URL}/{category_path}?page={page}"
        resp = await self._http_client.get(url)
        resp.raise_for_status()

        return self._extract_ads_list(resp.text)

    @http_retry
    async def _fetch_html(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        resp = await self._http_client.get(url)
        resp.raise_for_status()
        return resp.text

    # ─── Parsování ───────────────────────────────────────────────────────────

    @staticmethod
    def _extract_ads_list(html: str) -> Optional[Dict[str, Any]]:
        """Extrahuj adsListResult ze __NEXT_DATA__ JSON."""
        match = re.search(
            r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>',
            html,
            re.DOTALL,
        )
        if not match:
            return None
        try:
            nd = json_module.loads(match.group(1))
            return nd["props"]["pageProps"]["adsListResult"]
        except (KeyError, json_module.JSONDecodeError) as exc:
            logger.debug("Reas.cz __NEXT_DATA__ parse error: %s", exc)
            return None

    @staticmethod
    def _parse_description(html: str) -> Optional[str]:
        """Extrahuj popis inzerátu z detail stránky."""
        # Zkus __NEXT_DATA__ nejprve
        match = re.search(
            r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>',
            html,
            re.DOTALL,
        )
        if match:
            try:
                nd = json_module.loads(match.group(1))
                detail = nd["props"]["pageProps"].get("adEstateDetail") or {}
                desc = detail.get("description") or detail.get("text")
                if desc:
                    return str(desc).strip()[:4000]
            except Exception:
                pass

        # Fallback: BeautifulSoup – hledáme popisný blok
        soup = BeautifulSoup(html, "html.parser")
        for selector in [
            "[class*='description']",
            "[class*='Description']",
            "[class*='about']",
            "article p",
        ]:
            el = soup.select_one(selector)
            if el:
                text = el.get_text(" ", strip=True)
                if len(text) > 50:
                    return text[:4000]
        return None

    # ─── Sestavení listingu ───────────────────────────────────────────────────

    def _build_listing(
        self,
        ad: Dict[str, Any],
        offer_type: str,
        segment: str,
        description: Optional[str],
        locality_hint: str = "",
    ) -> Dict[str, Any]:
        """Sestaví normalizovaný dict inzerátu z SSR dat.
        
        locality_hint: volitelný suffix přidaný k location_text (např. 'Znojemský okres')
            pro zajištění průchodu geografickým filtrem v database.py.
            Sub-obce jako 'Oblekovice' neobsahují 'znojmo' a filtrem by neprošly.
        """
        external_id: str = ad["_id"]
        link: str = ad.get("link", f"{BASE_URL}/inzerat/{external_id}")

        # Typ nemovitosti
        reas_type = ad.get("type") or ad.get("subType") or "other"
        property_type = PROPERTY_TYPE_MAP.get(reas_type.lower(), "Other")

        # Titulek – sestavíme z dostupných polí
        disposition = ad.get("disposition") or ""
        area = ad.get("displayArea") or ad.get("floorArea")
        location_short = ad.get("formattedAddress") or ad.get("formattedLocation") or ""
        segment_name = SEGMENT_NAMES.get(segment, "nemovitosti")
        offer_word = "Pronájem" if offer_type == "Rent" else "Prodej"

        title_parts = [offer_word, segment_name]
        if disposition:
            title_parts.append(disposition)
        if area:
            title_parts.append(f"{area} m²")
        if location_short:
            title_parts.append(f"– {location_short}")
        title = " ".join(title_parts)[:200]

        # Cena
        price: Optional[float] = None
        raw_price = ad.get("price") or ad.get("originalPrice")
        if raw_price is not None:
            try:
                price = float(raw_price)
            except (ValueError, TypeError):
                pass

        # Plocha
        area_value: Optional[float] = None
        if area is not None:
            try:
                area_value = float(area)
            except (ValueError, TypeError):
                pass

        # Lokace – přidej locality_hint pokud subobec neobsahuje klíčové slovo
        location_text = (
            ad.get("formattedLocation")
            or ad.get("formattedAddress")
            or ad.get("municipalitySlug", "").replace("-", " ").title()
        )
        if locality_hint and locality_hint.lower() not in (location_text or "").lower():
            location_text = f"{location_text}, {locality_hint}" if location_text else locality_hint

        # GPS souřadnice [lng, lat] → latitude, longitude
        latitude: Optional[float] = None
        longitude: Optional[float] = None
        point = ad.get("point") or {}
        coords = point.get("coordinates")
        if coords and len(coords) >= 2:
            try:
                longitude = float(coords[0])
                latitude = float(coords[1])
            except (ValueError, TypeError):
                pass

        # Fotky – max 20, preferred: original, fallback: preview
        photos: List[str] = []
        for img in sorted(
            ad.get("imagesWithMetadata") or [],
            key=lambda x: x.get("order", 999),
        )[:20]:
            url = img.get("original") or img.get("preview")
            if url:
                photos.append(url)

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": external_id,
            "url": link,
            "title": title,
            "offer_type": offer_type,
            "property_type": property_type,
            "price": price,
            "area_built_up": area_value,
            "area_land": None,
            "location_text": location_text,
            "latitude": latitude,
            "longitude": longitude,
            "description": description,
            "photos": photos,
            "is_active": True,
        }

    # ─── Uložení do DB ────────────────────────────────────────────────────────

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db_manager = get_db_manager()
        await db_manager.upsert_listing(listing)
