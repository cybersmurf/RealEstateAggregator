"""
Prodejme.to scraper – Next.js RSC embedded data (since ~2026-06).

Site characteristics:
- Listings are embedded in the initial HTML RSC payload under "initialProperties".
- Parsing: extract self.__next_f.push([1,"..."]) chunks, unescape JS string,
  JSON-decode the initialProperties array, resolve $N description references.
- Detail URL: /nemovitosti/{slug}
- Photos: Supabase CDN
- No pagination – single GET returns all active listings.
"""
import json
import logging
import re
from typing import Any, Dict, List, Optional

import httpx

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.prodejme.to"
LISTINGS_PAGE_URL = f"{BASE_URL}/nemovitosti"
_RSC_PUSH_RE = re.compile(r'self\.__next_f\.push\(\[1,"((?:\\.|[^"])*)"\]\)')
_RSC_TEXT_RE = re.compile(r"([0-9a-f]+):T([0-9a-f]+),")
_PROPERTIES_MARKER = '"initialProperties":'

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9,en;q=0.8",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}

ADVERT_TYPE_MAP: Dict[int, str] = {
    1: "Byt",
    2: "Dům",
    3: "Pozemek",
    4: "Komerční",
    5: "Ostatní",
}

LISTING_TYPE_MAP: Dict[str, str] = {
    "SALE": "Prodej",
    "RENT": "Pronájem",
}

# Skip non-active listings (SOLD/RESERVED still appear in initialProperties).
_INACTIVE_STATUSES = frozenset({"SOLD", "RESERVED"})


class ProdejmeToScraper:
    """Scraper for Prodejme.to (Next.js RSC embedded listings)."""

    SOURCE_CODE = "PRODEJMETO"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        return await self.scrape()

    async def scrape(self) -> int:
        logger.info("Starting Prodejme.to scraper (RSC embedded initialProperties)")

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=90,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                with timer("Fetch /nemovitosti RSC payload"):
                    html = await self._fetch_page(LISTINGS_PAGE_URL)

                with timer("Parse initialProperties from RSC"):
                    raw_listings = self._parse_rsc_html(html)

                logger.info("Parsed %s listings from RSC payload", len(raw_listings))

                for raw in raw_listings:
                    try:
                        listing = self._map_listing(raw)
                        if listing is None:
                            continue
                        await self._save_listing(listing)
                        self.scraped_count += 1
                        metrics.increment_scraped()
                    except Exception as exc:
                        logger.error(
                            "Error processing listing %s: %s",
                            raw.get("id", "?"),
                            exc,
                        )
                        metrics.increment_failed()

                self._http_client = None

        logger.info("Prodejme.to scraper finished. Saved %s listings", self.scraped_count)
        return self.scraped_count

    async def _fetch_page(self, url: str) -> str:
        assert self._http_client is not None
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    @staticmethod
    def _unescape_rsc_chunk(chunk: str) -> str:
        """Decode JS string escapes from Next.js RSC push payload."""
        return chunk.encode("latin1", "backslashreplace").decode("unicode_escape")

    def _parse_rsc_html(self, html: str) -> List[Dict[str, Any]]:
        """Extract and parse the initialProperties array from RSC HTML."""
        chunks = _RSC_PUSH_RE.findall(html)
        if not chunks:
            logger.error("No self.__next_f.push chunks found in /nemovitosti HTML")
            return []

        combined = "".join(self._unescape_rsc_chunk(c) for c in chunks)
        marker_pos = combined.find(_PROPERTIES_MARKER)
        if marker_pos < 0:
            logger.error("initialProperties marker not found in RSC payload")
            return []

        start = marker_pos + len(_PROPERTIES_MARKER)
        while start < len(combined) and combined[start] in " \n":
            start += 1

        try:
            listings, _ = json.JSONDecoder().raw_decode(combined, start)
        except json.JSONDecodeError as exc:
            logger.error("Failed to parse initialProperties JSON: %s", exc)
            return []

        if not isinstance(listings, list):
            logger.error("initialProperties is not a list: %s", type(listings))
            return []

        text_map = self._build_text_reference_map(combined, marker_pos)
        self._resolve_description_references(listings, text_map)
        return listings

    @staticmethod
    def _build_text_reference_map(combined: str, marker_pos: int) -> Dict[str, str]:
        """Build map of RSC text-chunk IDs to description strings."""
        text_map: Dict[str, str] = {}
        for match in _RSC_TEXT_RE.finditer(combined, 0, marker_pos):
            record_id = match.group(1)
            text_len = int(match.group(2), 16)
            text_start = match.end()
            text_end = text_start + text_len
            if text_end <= len(combined):
                text_map[record_id] = combined[text_start:text_end]
        return text_map

    @staticmethod
    def _resolve_description_references(
        listings: List[Dict[str, Any]],
        text_map: Dict[str, str],
    ) -> None:
        for listing in listings:
            desc = listing.get("description")
            if isinstance(desc, str) and desc.startswith("$"):
                ref_id = desc[1:]
                listing["description"] = text_map.get(ref_id, "")

    def _map_listing(self, raw: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """Map a raw prodejme.to listing dict to our DB schema dict."""
        listing_id = raw.get("id", "")
        status = raw.get("status", "") or raw.get("statust", "")

        if status in _INACTIVE_STATUSES:
            return None

        slug = raw.get("slug", "")
        if not slug or not listing_id:
            return None

        listing_type = raw.get("listingType", "SALE")
        offer_type = LISTING_TYPE_MAP.get(listing_type, "Prodej")
        property_type = self._infer_property_type(raw)

        city = raw.get("localityCity") or ""
        region = raw.get("localityRegion") or ""
        if city and region:
            location_text = f"{city}, {region}"
        elif city:
            location_text = city
        elif region:
            location_text = region
        else:
            estate = (raw.get("sourcePayload") or {}).get("estate") or {}
            location_text = estate.get("locality_address", "")

        price: Optional[int] = None
        raw_price = raw.get("price")
        if raw_price is not None:
            try:
                price = int(raw_price) if int(raw_price) > 0 else None
            except (TypeError, ValueError):
                pass

        area_built_up: Optional[int] = None
        raw_area = raw.get("area")
        if raw_area:
            try:
                area_built_up = int(raw_area) if int(raw_area) > 0 else None
            except (TypeError, ValueError):
                pass

        area_land: Optional[int] = None
        raw_land = raw.get("landArea")
        if raw_land:
            try:
                area_land = int(raw_land) if int(raw_land) > 0 else None
            except (TypeError, ValueError):
                pass

        description = (raw.get("description") or "")[:5000]
        images = raw.get("images") or []
        photos = [url for url in images if isinstance(url, str) and url.startswith("http")][:20]

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": listing_id,
            "url": f"{BASE_URL}/nemovitosti/{slug}",
            "title": (raw.get("title") or "")[:200],
            "description": description,
            "price": price,
            "offer_type": offer_type,
            "property_type": property_type,
            "area_built_up": area_built_up,
            "area_land": area_land,
            "location_text": location_text,
            "photos": photos,
        }

    @staticmethod
    def _infer_property_type(raw: Dict[str, Any]) -> str:
        """Infer Czech property type from sourcePayload or propertyType label."""
        source_payload = raw.get("sourcePayload") or {}
        estate = source_payload.get("estate") or {}
        raw_advert_type = estate.get("advert_type")
        if raw_advert_type is not None:
            try:
                return ADVERT_TYPE_MAP.get(int(raw_advert_type), "Ostatní")
            except (TypeError, ValueError):
                pass

        label = (raw.get("propertyType") or "").lower()
        if not label:
            return "Ostatní"

        if any(k in label for k in ("rodinn", "vícegenerační", "vicegeneracni")):
            return "Dům"
        if any(k in label for k in ("+kk", "+1", "+2", "+3", "+4", "+5", "byt", "apartmán", "apartman")):
            return "Byt"
        if any(k in label for k in ("zahrada", "pozemek", "louka", "pole", "les")):
            return "Pozemek"
        if any(k in label for k in ("chata", "chalupa")):
            return "Chata"
        if any(k in label for k in ("garáž", "garaz")):
            return "Ostatní"
        if any(k in label for k in ("komerční", "kancelá", "kancela", "výroba", "sklad")):
            return "Komerční"
        if "bydlení" in label or "bydleni" in label:
            return "Dům"

        return "Ostatní"

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kc",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
