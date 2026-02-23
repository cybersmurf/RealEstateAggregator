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
from ..http_utils import http_retry

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
        locality_district_id: Optional[int] = None,
        per_page: int = 60,
        fetch_details: bool = True,
        detail_fetch_concurrency: int = 5,
    ) -> None:
        self.category_main_cb = category_main_cb
        self.category_type_cb = category_type_cb
        self.locality_region_id = locality_region_id
        self.locality_district_id = locality_district_id
        self.per_page = per_page
        self.fetch_details = fetch_details
        self.detail_fetch_concurrency = detail_fetch_concurrency  # 🔥 Semaphore limit
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 999 if full_rescan else 5
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 5) -> int:
        logger.info(
            "Starting Sreality scraper (category_main=%s, type=%s, region=%s, max_pages=%s, detail_concurrency=%s)",
            self.category_main_cb,
            CATEGORY_TYPE_MAP.get(self.category_type_cb, self.category_type_cb),
            REGION_NAMES.get(self.locality_region_id, self.locality_region_id),
            max_pages,
            self.detail_fetch_concurrency,
        )

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                page = 1
                
                # 🔥 Semaphore pro batch detail fetching
                semaphore = asyncio.Semaphore(self.detail_fetch_concurrency)

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

                        # 🔥 Batch process estates se semaphore
                        await self._process_estates_batch(estates, semaphore, metrics)

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

    @http_retry
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
        if self.locality_district_id is not None:
            params["locality_district_id"] = self.locality_district_id

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

    async def _process_estates_batch(self, estates: List[Dict[str, Any]], 
                                     semaphore: asyncio.Semaphore, metrics) -> None:
        """
        Process a batch of estates with concurrent detail fetching.
        
        🔥 Uses asyncio.Semaphore to limit concurrent detail requests to avoid:
        - Rate limiting (429 errors)
        - Connection timeouts
        - Server overload
        
        Args:
            estates: List of estate dicts from API
            semaphore: asyncio.Semaphore(max_concurrent) to limit concurrent requests
            metrics: Metrics context for tracking
        """
        tasks = []
        
        for estate in estates:
            task = asyncio.create_task(
                self._process_estate_with_semaphore(estate, semaphore, metrics)
            )
            tasks.append(task)
        
        # 🔥 Wait for all tasks with semaphore-controlled concurrency
        results = await asyncio.gather(*tasks, return_exceptions=True)
        
        # Handle results
        for idx, result in enumerate(results):
            if isinstance(result, Exception):
                logger.error("Error processing estate batch item %s: %s", idx, result)
                metrics.increment_failed()
            elif result is not None:
                self.scraped_count += 1
                metrics.increment_scraped()
    
    async def _process_estate_with_semaphore(self, estate: Dict[str, Any],
                                             semaphore: asyncio.Semaphore,
                                             metrics) -> bool:
        """
        Process single estate with semaphore-controlled detail fetch.
        
        Args:
            estate: Estate dict from list API
            semaphore: Semaphore to limit concurrent requests
            metrics: Metrics context
            
        Returns:
            True if saved successfully, False otherwise
        """
        try:
            normalized = await self._process_estate(estate, semaphore)
            if normalized:
                await self._save_listing(normalized)
                return True
            return False
        except Exception as exc:
            logger.error(
                "Error processing estate %s: %s",
                estate.get("hash_id"),
                exc,
            )
            return False
    
    async def _process_estate(self, estate: Dict[str, Any],
                             semaphore: Optional[asyncio.Semaphore] = None) -> Optional[Dict[str, Any]]:
        """
        Process estate and fetch detailed info with optional semaphore control.
        
        Args:
            estate: Estate dict from list API
            semaphore: Optional semaphore to limit concurrent detail requests
            
        Returns:
            Normalized estate dict or None
        """
        hash_id = estate.get("hash_id")
        if not hash_id:
            return None

        normalized = self._normalize_list_item(estate)

        if self.fetch_details:
            # 🔥 Fetch detail with optional semaphore control
            detail = await self._fetch_estate_detail_with_semaphore(hash_id, semaphore)
            if detail:
                normalized = self._merge_detail(normalized, detail)

        return normalized
    
    async def _fetch_estate_detail_with_semaphore(self, hash_id: int,
                                                  semaphore: Optional[asyncio.Semaphore] = None
                                                  ) -> Optional[Dict[str, Any]]:
        """
        Fetch estate detail with optional semaphore to limit concurrency.
        
        🔥 If semaphore provided: acquires permit before fetch, ensuring max concurrent requests
        
        Args:
            hash_id: Estate hash ID
            semaphore: Optional asyncio.Semaphore to limit concurrent requests
            
        Returns:
            Detail dict or None
        """
        if semaphore is None:
            # No semaphore - unlimited concurrent (old behavior)
            return await self._fetch_estate_detail(hash_id)
        
        # 🔥 Acquire semaphore permit - limits concurrent requests
        async with semaphore:
            return await self._fetch_estate_detail(hash_id)

    # Mapping category_main_cb → slug (singular, SReality URL format)
    _CAT_MAIN_SLUG = {
        1: "byt",
        2: "dum",
        3: "pozemek",
        4: "komercni",
        5: "ostatni",
    }

    # Mapping category_sub_cb → slug (SReality URL sub-type)
    _CAT_SUB_SLUG = {
        # Byty (cat_main=1)
        2: "1+kk", 3: "1+1", 4: "2+kk", 5: "2+1",
        6: "3+kk", 7: "3+1", 8: "4+kk", 9: "4+1",
        10: "5+kk", 11: "5+1", 12: "6-a-vice", 16: "atypicke",
        # Domy (cat_main=2)
        37: "rodinny", 39: "chata", 33: "vila",
        38: "zemedelska-usedlost", 41: "jiny", 43: "radovy",
        44: "bungalov", 45: "bytovy-dum", 46: "atypicky",
        # Pozemky (cat_main=3)
        17: "bydleni", 18: "zemedelsky", 19: "komercni",
        21: "ostatni", 22: "les", 23: "rybniky", 26: "vinice-sad",
        # Komerční (cat_main=4)
        27: "kancelar", 28: "sklad", 29: "vyroba", 30: "obchodni",
        31: "ubytovani", 32: "restaurace", 34: "zemedelsky",
        35: "jina", 36: "bytovy-dum",
        # Ostatní (cat_main=5)
        24: "garaz", 25: "stani", 40: "parkovaci-misto",
    }

    def _normalize_list_item(self, estate: Dict[str, Any]) -> Dict[str, Any]:
        hash_id = estate.get("hash_id")

        seo = estate.get("seo", {})
        cat_main = seo.get("category_main_cb", self.category_main_cb or 2)
        cat_sub  = seo.get("category_sub_cb")
        cat_type = seo.get("category_type_cb", self.category_type_cb)
        locality_slug = seo.get("locality", "")

        cat_main_slug = self._CAT_MAIN_SLUG.get(cat_main, "dum")
        cat_sub_slug  = self._CAT_SUB_SLUG.get(cat_sub, "") if cat_sub else ""
        cat_type_slug = {1: "prodej", 2: "pronajem", 3: "drazba"}.get(cat_type, "prodej")

        # Build full canonical URL: /detail/{type}/{main}/{sub}/{locality}/{hash_id}
        # Falls back gracefully when sub/locality are missing
        if cat_sub_slug and locality_slug:
            detail_url = f"{BASE_WEB}/detail/{cat_type_slug}/{cat_main_slug}/{cat_sub_slug}/{locality_slug}/{hash_id}"
        elif locality_slug:
            detail_url = f"{BASE_WEB}/detail/{cat_type_slug}/{cat_main_slug}/{locality_slug}/{hash_id}"
        else:
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
        # 🔥 SReality API vrací 'text' jako dict {'name': 'Popis', 'value': '...'} nebo string
        description_raw = detail.get("text") or detail.get("description")
        if description_raw:
            if isinstance(description_raw, dict):
                description = description_raw.get("value", "")
            elif isinstance(description_raw, str):
                description = description_raw
            else:
                description = ""
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
        # 🔥 re.sub místo isdigit() – isdigit() vrací True pro Unicode ² (²)
        import re
        digits = re.sub(r"[^0-9]", "", str(value))
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
