"""
Prodejme.to scraper – Next.js rewrite (since ~2026-05).

Site characteristics:
- Website migrated from PHP/AJAX to Next.js (React Server Components, Vercel).
- All listings are returned in a single Next.js Server Action call.
  POST /nemovitosti  with header  Next-Action: {action_id}
  Body: [{}]  (empty args → return all listings)
  Response: RSC stream format; the JSON array of listings is found by
  locating the unique "1:[" marker in the stream.
- Action ID is discovered dynamically from the JS chunk that references
  createServerReference("ID", ..., "useProperties").
- Detail URL: /nemovitosti/{slug}
- Photos: Supabase CDN  https://hqrcqgyjunwvdafvzfbr.supabase.co/storage/...
- No pagination needed – single call returns all ~239 listings.
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

# Fallback action ID – updated automatically when _discover_action_id() runs.
# Format: SHA256-like hex string produced by Next.js at build time.
_KNOWN_ACTION_ID = "007dad02ed5484c9c6ac9d6c6e9be2fb1c70995253"
_SERVER_ACTION_NAME = "useProperties"

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9,en;q=0.8",
    "Accept": "*/*",
}

# advert_type → property_type mapping (from prodejme.to's JS frontend)
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


class ProdejmeToScraper:
    """Scraper for Prodejme.to (Next.js site, Server Action API)."""

    SOURCE_CODE = "PRODEJMETO"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        return await self.scrape()

    async def scrape(self) -> int:
        logger.info("Starting Prodejme.to scraper (Next.js Server Action)")

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=60,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                action_id = await self._discover_action_id()
                logger.info("Using Server Action ID: %s", action_id)

                with timer("Fetch all listings via Server Action"):
                    raw_listings = await self._fetch_all_listings(action_id)

                logger.info("Fetched %s listings from Server Action", len(raw_listings))

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

    # ------------------------------------------------------------------
    # Action ID discovery
    # ------------------------------------------------------------------

    async def _discover_action_id(self) -> str:
        """
        Dynamically discover the Next.js Server Action ID by downloading
        the /nemovitosti page and then the JS chunk that contains the
        createServerReference call for "useProperties".

        Falls back to _KNOWN_ACTION_ID if discovery fails.
        """
        global _KNOWN_ACTION_ID
        try:
            assert self._http_client is not None
            # 1. Fetch the page HTML to get JS chunk URLs
            resp = await self._http_client.get(LISTINGS_PAGE_URL)
            resp.raise_for_status()
            html = resp.text

            # 2. Find all _next/static/chunks/*.js URLs
            chunk_urls = re.findall(
                r'"(/_next/static/chunks/[a-f0-9]+\.js[^"]*)"', html
            )
            if not chunk_urls:
                logger.warning("No JS chunks found in /nemovitosti HTML, using fallback action ID")
                return _KNOWN_ACTION_ID

            # 3. Scan chunks for createServerReference("...", ..., "useProperties")
            for chunk_path in chunk_urls[:30]:  # limit to first 30 chunks
                chunk_url = BASE_URL + chunk_path.split("?")[0]
                try:
                    chunk_resp = await self._http_client.get(chunk_url, timeout=15)
                    if chunk_resp.status_code != 200:
                        continue
                    chunk_text = chunk_resp.text
                    m = re.search(
                        r'createServerReference\)\("([0-9a-f]{30,60})"[^)]*"'
                        + re.escape(_SERVER_ACTION_NAME)
                        + r'"',
                        chunk_text,
                    )
                    if m:
                        discovered = m.group(1)
                        logger.info("Discovered Server Action ID: %s", discovered)
                        _KNOWN_ACTION_ID = discovered
                        return discovered
                except Exception as chunk_exc:
                    logger.debug("Error fetching chunk %s: %s", chunk_url, chunk_exc)

            logger.warning(
                "Could not discover Server Action ID from JS chunks, using known ID: %s",
                _KNOWN_ACTION_ID,
            )
        except Exception as exc:
            logger.warning("Action ID discovery failed (%s), using known ID: %s", exc, _KNOWN_ACTION_ID)

        return _KNOWN_ACTION_ID

    # ------------------------------------------------------------------
    # Fetch listings via Server Action
    # ------------------------------------------------------------------

    async def _fetch_all_listings(self, action_id: str) -> List[Dict[str, Any]]:
        """
        Call the Next.js Server Action and parse the RSC stream response.
        Returns the raw list of listing dicts from prodejme.to's DB.
        """
        assert self._http_client is not None
        response = await self._http_client.post(
            LISTINGS_PAGE_URL,
            headers={
                "Next-Action": action_id,
                "Content-Type": "application/json",
            },
            content=b"[{}]",
            timeout=60,
        )
        response.raise_for_status()
        return self._parse_rsc_response(response.content)

    def _parse_rsc_response(self, content: bytes) -> List[Dict[str, Any]]:
        """
        Parse the React Server Components (RSC) stream response.

        The stream contains:
          - Record 0: metadata JSON
          - Records 2+: text chunks (descriptions) in format {id}:T{hexlen},{bytes}
          - Record 1: the main result – a JSON array of listing dicts,
            where "description" fields are references like "$2" pointing
            to the text chunks above.

        Strategy: locate the unique b'1:[{' marker and parse the JSON array
        from that position. Then build a text reference map from RSC text
        records to resolve '$N' description references.
        """
        # 1. Locate the JSON array (record "1")
        marker = b"1:["
        pos = content.find(marker)
        if pos < 0:
            logger.error("Could not find '1:[' in RSC response (size=%d)", len(content))
            return []

        json_bytes = content[pos + 2:]  # skip "1:"
        try:
            listings = json.loads(json_bytes)
        except json.JSONDecodeError as exc:
            logger.error("Failed to parse listings JSON: %s", exc)
            return []

        if not isinstance(listings, list):
            logger.error("Expected JSON array, got %s", type(listings))
            return []

        # 2. Build text reference map: {hex_id_str -> description_text}
        # RSC text records: {hex_id}:T{hexlen},{text}
        text_map: Dict[str, str] = {}
        text_pattern = re.compile(rb"([0-9a-f]+):T([0-9a-f]+),")
        search_limit = min(pos, len(content))  # only look before the JSON array
        for m in text_pattern.finditer(content, 0, search_limit):
            record_id = m.group(1).decode()
            text_len = int(m.group(2), 16)
            text_start = m.end()
            text_end = text_start + text_len
            if text_end <= len(content):
                text_map[record_id] = content[text_start:text_end].decode("utf-8", errors="replace")

        # 3. Resolve "$N" references in each listing's description field
        for listing in listings:
            desc = listing.get("description")
            if isinstance(desc, str) and desc.startswith("$"):
                ref_id = desc[1:]
                if ref_id in text_map:
                    listing["description"] = text_map[ref_id]
                else:
                    listing["description"] = ""

        logger.debug("Parsed %d listings from RSC (text refs: %d)", len(listings), len(text_map))
        return listings

    # ------------------------------------------------------------------
    # Field mapping
    # ------------------------------------------------------------------

    def _map_listing(self, raw: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """Map a raw prodejme.to listing dict to our DB schema dict."""
        listing_id = raw.get("id", "")
        status = raw.get("status", "") or raw.get("statust", "")

        # Skip sold listings (we don't store them)
        if status == "SOLD":
            return None

        slug = raw.get("slug", "")
        if not slug or not listing_id:
            return None

        listing_type = raw.get("listingType", "SALE")
        offer_type = LISTING_TYPE_MAP.get(listing_type, "Prodej")

        # Property type from sourcePayload.estate.advert_type (most reliable)
        advert_type: Optional[int] = None
        source_payload = raw.get("sourcePayload") or {}
        estate = source_payload.get("estate") or {}
        raw_advert_type = estate.get("advert_type")
        if raw_advert_type is not None:
            try:
                advert_type = int(raw_advert_type)
            except (TypeError, ValueError):
                pass
        property_type = ADVERT_TYPE_MAP.get(advert_type or 0, "Ostatní")

        # Location: combine city + region for geo filter compatibility
        city = raw.get("localityCity") or ""
        region = raw.get("localityRegion") or ""
        if city and region:
            location_text = f"{city}, {region}"
        elif city:
            location_text = city
        elif region:
            location_text = region
        else:
            location_text = estate.get("locality_address", "")

        # Price
        price: Optional[int] = None
        raw_price = raw.get("price")
        if raw_price is not None:
            try:
                price = int(raw_price) if int(raw_price) > 0 else None
            except (TypeError, ValueError):
                pass

        # Area
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

        # Description
        description = (raw.get("description") or "")[:5000]

        # Photos (Supabase CDN URLs, already absolute)
        images = raw.get("images") or []
        photos = [url for url in images if isinstance(url, str) and url.startswith("http")][:20]

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": listing_id,  # prodejme.to UUID (stable DB primary key)
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

    # ------------------------------------------------------------------
    # Persistence
    # ------------------------------------------------------------------

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kc",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
