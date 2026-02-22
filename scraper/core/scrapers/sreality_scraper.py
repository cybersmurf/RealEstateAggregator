"""
Sreality.cz scraper - direct JSON REST API.

API endpoint: https://www.sreality.cz/api/cs/v2/estates
- No authentication required
- Paginated JSON
- Detail: https://www.sreality.cz/api/cs/v2/estates/{hash_id}
"""
import asyncio
import logging
import time
from typing import Any, Dict, List, Optional

import httpx

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)

CATEGORY_MAIN_MAP = {
    1: "Byt",
    2: "Dům",
    3: "Pozemek",
    4: "Komerční",
    5: "Ostatní",
}

CATEGORY_TYPE_MAP = {
    1: "Prodej",
    2: "Pronájem",
    3: "Dražba",
}

REGION_NAMES = {
    10: "Praha",
    11: "Středočeský kraj",
    12: "Jihočeský kraj",
    13: "Plzeňský kraj",
    14: "Jihomoravský kraj",
    15: "Ústecký kraj",
    16: "Liberecký kraj",
    17: "Královéhradecký kraj",
    18: "Pardubický kraj",
    19: "Kraj Vysočina",
    20: "Olomoucký kraj",
    21: "Zlínský kraj",
    22: "Moravskoslezský kraj",
    23: "Karlovarský kraj",
}

BASE_API = "https://www.sreality.cz/api/cs/v2"
BASE_WEB = "https://www.sreality.cz"

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "application/json, text/plain, */*",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": "https://www.sreality.cz/",
}


class SrealityScraper:
    """Scraper for Sreality.cz using its JSON REST API."""

    SOURCE_CODE = "SREALITY"

    def __init__(
        self,
        category_main_cb: Optional[int] = None,
        category_type_cb: int = 1,
        locality_region_id: Optional[int] = None,
        per_page: int = 60,
        fetch_details: bool = True,
    ) -> None:
        self.category_main_cb = category_main_cb
        self.category_type_cb = category_type_cb
        self.locality_region_id = locality_region_id
        self.per_page = per_page
        self.fetch_details = fetch_details
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 999 if full_rescan else 5
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 5) -> int:
        logger.info(
            "Starting Sreality scraper (category_main=%s, type=%s, region=%s, max_pages=%s)",
            self.category_main_cb,
            CATEGORY_TYPE_MAP.get(self.category_type_cb, self.category_type_cb),
            REGION_NAMES.get(self.locality_region_id, self.locality_region_id),
            max_pages,
        )

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                page = 1

                while page <= max_pages:
                    try:
                        with timer(f"Fetch list page {page}"):
                            start = time.perf_counter()
                            data = await self._fetch_listings_page(page)
                            metrics.record_fetch(time.perf_counter() - start)

                        if data is None:
                            logger.error("API returned None for page %s, stopping", page)
                            break

                        estates = data.get("_embedded", {}).get("estates", [])
                        if not estates:
                            logger.info("No estates on page %s, stopping", page)
                            break

                        result_size = data.get("result_size", 0)
                        logger.info(
                            "Page %s: got %s estates (total available: %s)",
                            page,
                            len(estates),
                            result_size,
                        )

                        for estate in estates:
                            try:
                                with timer(f"Process estate {estate.get('hash_id')}"):
                                    normalized = await self._process_estate(estate)
                                if normalized:
                                    await self._save_listing(normalized)
                                    self.scraped_count += 1
                                    metrics.increment_scraped()
                            except Exception as exc:
                                logger.error(
                                    "Error processing estate %s: %s",
                                    estate.get("hash_id"),
                                    exc,
                                )
                                metrics.increment_failed()

                        total_pages = (result_size + self.per_page - 1) // self.per_page
                        if page >= total_pages:
                            logger.info("Reached last page (%s/%s)", page, total_pages)
                            break

                        page += 1
                        await asyncio.sleep(1.0)

                    except httpx.HTTPStatusError as exc:
                        logger.error("HTTP error on page %s: %s", page, exc)
                        if exc.response.status_code == 429:
                            logger.warning("Rate limited - waiting 30s")
                            await asyncio.sleep(30)
                            continue
                        break
                    except Exception as exc:
                        logger.error("Unexpected error on page %s: %s", page, exc)
                        metrics.increment_failed()
                        break

                self._http_client = None

        logger.info("Sreality scraper finished. Scraped %s listings", self.scraped_count)
        return self.scraped_count

    async def _fetch_listings_page(self, page: int) -> Optional[Dict[str, Any]]:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        params: Dict[str, Any] = {
            "per_page": self.per_page,
            "page": page,
        }
        if self.category_main_cb is not None:
            params["category_main_cb"] = self.category_main_cb
        if self.category_type_cb is not None:
            params["category_type_cb"] = self.category_type_cb
        if self.locality_region_id is not None:
            params["locality_region_id"] = self.locality_region_id

        url = f"{BASE_API}/estates"
        logger.debug("GET %s params=%s", url, params)

        response = await self._http_client.get(url, params=params)
        response.raise_for_status()
        return response.json()

    async def _fetch_estate_detail(self, hash_id: int) -> Optional[Dict[str, Any]]:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        url = f"{BASE_API}/estates/{hash_id}"
        logger.debug("GET detail: %s", url)

        try:
            response = await self._http_client.get(url)
            response.raise_for_status()
            await asyncio.sleep(0.3)
            return response.json()
        except httpx.HTTPStatusError as exc:
            logger.warning("Detail fetch failed for %s: %s", hash_id, exc)
            return None

    async def _process_estate(self, estate: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        hash_id = estate.get("hash_id")
        if not hash_id:
            return None

        normalized = self._normalize_list_item(estate)

        if self.fetch_details:
            detail = await self._fetch_estate_detail(hash_id)
            if detail:
                normalized = self._merge_detail(normalized, detail)

        return normalized

    def _normalize_list_item(self, estate: Dict[str, Any]) -> Dict[str, Any]:
        hash_id = estate.get("hash_id")

        seo = estate.get("seo", {})
        cat_main = seo.get("category_main_cb", self.category_main_cb or 2)
        cat_type = seo.get("category_type_cb", self.category_type_cb)

        cat_main_slug = {
            1: "byty",
            2: "domy",
            3: "pozemky",
            4: "komercni",
            5: "ostatni",
        }.get(cat_main, "domy")
        cat_type_slug = {1: "prodej", 2: "pronajem", 3: "drazba"}.get(cat_type, "prodej")

        detail_url = f"{BASE_WEB}/detail/{cat_type_slug}/{cat_main_slug}/{hash_id}"

        price_czk = estate.get("price_czk") or {}
        price_raw = price_czk.get("value_raw")
        price = price_raw if (price_raw and price_raw > 1) else None

        gps = estate.get("gps") or {}

        links = estate.get("_links") or {}
        images = links.get("images") or []
        photos = [img.get("href") for img in images if img.get("href")]

        property_type = CATEGORY_MAIN_MAP.get(cat_main, "Ostatni")
        offer_type = CATEGORY_TYPE_MAP.get(cat_type, "Prodej")

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": str(hash_id),
            "url": detail_url,
            "title": (estate.get("name") or "")[:200],
            "location_text": estate.get("locality", ""),
            "price": price,
            "property_type": property_type,
            "offer_type": offer_type,
            "latitude": gps.get("lat"),
            "longitude": gps.get("lon"),
            "photos": photos[:20],
        }

    def _merge_detail(self, normalized: Dict[str, Any], detail: Dict[str, Any]) -> Dict[str, Any]:
        description = detail.get("text") or detail.get("description")
        if description:
            normalized["description"] = description[:5000]

        detail_photos = self._extract_photos(detail)
        if detail_photos:
            normalized["photos"] = detail_photos[:20]

        params = self._extract_params(detail)
        if params:
            normalized["area_built_up"] = self._parse_area(params.get("Užitná plocha"))
            normalized["area_land"] = self._parse_area(params.get("Plocha pozemku"))

        return normalized

    def _extract_photos(self, detail: Dict[str, Any]) -> List[str]:
        photos: List[str] = []

        links = detail.get("_links") or {}
        for img in links.get("images") or []:
            href = img.get("href")
            if href:
                photos.append(href)

        embedded = detail.get("_embedded") or {}
        for img in embedded.get("images") or []:
            href = img.get("href") or img.get("url")
            if href:
                photos.append(href)

        return list(dict.fromkeys(photos))

    def _extract_params(self, detail: Dict[str, Any]) -> Dict[str, str]:
        params: Dict[str, str] = {}

        for item in detail.get("items") or []:
            name = item.get("name")
            value = item.get("value")
            if not name or value is None:
                continue
            normalized = self._normalize_param_name(str(name))
            params[normalized] = str(value)

        return params

    @staticmethod
    def _normalize_param_name(name: str) -> str:
        lower = name.strip().lower()
        if "uzit" in lower or "užit" in lower:
            return "Užitná plocha"
        if "pozem" in lower:
            return "Plocha pozemku"
        return name.strip()

    @staticmethod
    def _parse_area(value: Optional[str]) -> Optional[int]:
        if not value:
            return None
        digits = "".join(c for c in value if c.isdigit())
        return int(digits) if digits else None

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kc",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
