"""
Lexamo scraper – lexamo.cz
Realitní kancelář Jihomoravský kraj / Znojemsko
Webflow SSR – httpx + BeautifulSoup
"""
import logging
import re
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin, urlencode

import httpx
from bs4 import BeautifulSoup

from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.lexamo.cz"
LISTINGS_URL = f"{BASE_URL}/"

HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Accept": "text/html,application/xhtml+xml,*/*;q=0.8",
}

OFFER_TYPE_MAP = {
    "prodej": "Sale",
    "pronájem": "Rent",
    "pronajem": "Rent",
}

PROPERTY_TYPE_MAP = {
    "byt": "Apartment",
    "bytov": "Apartment",
    "apartmán": "Apartment",
    "dům": "House",
    "domu": "House",
    "rodinný": "House",
    "rodinne": "House",
    "vila": "House",
    "pozemek": "Land",
    "pozemku": "Land",
    "parcela": "Land",
    "zahrada": "Land",
    "stavební": "Land",
    "specifický pozemek": "Land",
    "komerční": "Commercial",
    "sklady": "Commercial",
    "kanceláře": "Commercial",
    "chata": "Cottage",
    "chalupa": "Cottage",
    "rekreace": "Cottage",
}


class LexamoScraper:
    SOURCE_CODE = "LEXAMO"

    async def run(self, full_rescan: bool = False) -> int:
        """Run the scraper and return count of processed listings."""
        logger.info(f"[{self.SOURCE_CODE}] Starting scrape (full_rescan={full_rescan})")
        try:
            return await self.scrape()
        except Exception as e:
            logger.error(f"[{self.SOURCE_CODE}] Fatal error: {e}", exc_info=True)
            return 0

    async def scrape(self) -> int:
        """Fetch listings from homepage (and further pages), then scrape details."""
        async with httpx.AsyncClient(headers=HEADERS, follow_redirects=True, timeout=30) as client:
            detail_urls = await self._get_listing_urls(client)
            logger.info(f"[{self.SOURCE_CODE}] Found {len(detail_urls)} listings")

            count = 0
            db = get_db_manager()
            for url in detail_urls:
                try:
                    item = await self._parse_detail(client, url)
                    if item:
                        await db.upsert_listing(item)
                        count += 1
                        logger.debug(f"[{self.SOURCE_CODE}] Saved: {item.get('title','?')}")
                except Exception as e:
                    logger.warning(f"[{self.SOURCE_CODE}] Error parsing {url}: {e}")

        logger.info(f"[{self.SOURCE_CODE}] Done – {count} listings saved")
        return count

    # ------------------------------------------------------------------
    # Private helpers
    # ------------------------------------------------------------------

    async def _get_listing_urls(self, client: httpx.AsyncClient) -> List[str]:
        """Collect all unique detail URLs from the homepage (Webflow CMS)."""
        urls = []
        seen: set = set()
        page = 1

        while True:
            page_url = LISTINGS_URL if page == 1 else f"{LISTINGS_URL}?65cdb0cc_page={page}"
            resp = await client.get(page_url)
            resp.raise_for_status()
            soup = BeautifulSoup(resp.text, "html.parser")

            new_found = False
            for a in soup.select("a[href*='/realman-listing/']"):
                href = a.get("href", "").strip()
                if not href:
                    continue
                full_url = urljoin(BASE_URL, href)
                if full_url not in seen:
                    seen.add(full_url)
                    urls.append(full_url)
                    new_found = True

            # Stop if no new listings found (no more pages)
            if not new_found or page >= 20:
                break
            page += 1

        return urls

    async def _parse_detail(
        self, client: httpx.AsyncClient, url: str
    ) -> Optional[Dict[str, Any]]:
        """Fetch and parse a single property detail page."""
        resp = await client.get(url)
        resp.raise_for_status()
        soup = BeautifulSoup(resp.text, "html.parser")

        # External ID = numeric suffix at end of URL slug
        id_match = re.search(r"-(\d+)/?$", url)
        external_id = id_match.group(1) if id_match else url

        # Title – find heading containing "Prodej" or "Pronájem"
        title = ""
        for tag in soup.find_all(re.compile(r"^h[1-4]$")):
            txt = tag.get_text(strip=True)
            if re.search(r"prodej|pronájem|pronajem", txt, re.I) and len(txt) > 5:
                title = txt
                break

        if not title:
            logger.warning(f"[{self.SOURCE_CODE}] No title at {url}")
            return None

        # Offer type
        offer_type = self._detect_offer_type(title)

        # Property type
        property_type = self._detect_property_type(title, url)

        # Price
        price = self._extract_price(soup)

        # Location text – heading after the title heading
        location = self._extract_location(soup, title)

        # Parameters (Užitná plocha, Celková plocha, …)
        params = self._extract_params(soup)
        area = params.get("uzitna_plocha") or params.get("celkova_plocha")

        # Description
        description = self._extract_description(soup)

        # Photos
        photos = self._extract_photos(soup)

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": external_id,
            "url": url,
            "title": title,
            "description": description,
            "price": price,
            "offer_type": offer_type,
            "property_type": property_type,
            "area": area,
            "location_text": location,
            "photos": photos,
        }

    # ------------------------------------------------------------------
    # Field extractors
    # ------------------------------------------------------------------

    def _detect_offer_type(self, title: str) -> str:
        lower = title.lower()
        for keyword, otype in OFFER_TYPE_MAP.items():
            if keyword in lower:
                return otype
        return "Sale"

    def _detect_property_type(self, title: str, url: str) -> str:
        text = (title + " " + url).lower()
        for keyword, ptype in PROPERTY_TYPE_MAP.items():
            if keyword in text:
                return ptype
        return "Other"

    def _extract_price(self, soup: BeautifulSoup) -> Optional[float]:
        """Extract price from heading or paragraph containing Kč."""
        for tag in soup.find_all(re.compile(r"^h[1-4]$")):
            txt = tag.get_text(strip=True)
            if "Kč" in txt and re.search(r"\d", txt):
                nums = re.sub(r"[^\d]", "", txt)
                if nums and int(nums) > 1000:
                    return float(int(nums))
        return None

    def _extract_location(self, soup: BeautifulSoup, title: str) -> str:
        """Find location – typically a sibling heading after the title heading."""
        headings = soup.find_all(re.compile(r"^h[1-4]$"))
        for i, tag in enumerate(headings):
            if tag.get_text(strip=True) == title:
                # Next heading sibling is usually the location
                for j in range(i + 1, min(i + 4, len(headings))):
                    loc = headings[j].get_text(strip=True)
                    # Location should be a short city/street name, not price or title
                    if loc and "Kč" not in loc and len(loc) < 80 and not re.search(r"prodej|pronájem", loc, re.I):
                        return loc
                break
        # Fallback: find something that looks like a Czech city
        for el in soup.find_all(string=re.compile(r"Znojmo|Morašice|Jaroslavice|Višňové|Hevlín|Přítluky", re.I)):
            txt = el.strip()
            if 2 < len(txt) < 60:
                return txt
        return ""

    def _extract_params(self, soup: BeautifulSoup) -> Dict[str, Optional[float]]:
        """Extract parameter table values (area etc.)."""
        params: Dict[str, Optional[float]] = {}

        # Pattern: label text followed by value text
        # "Užitná plocha → 175 m²" or "Celková plocha → 2.059 m²"
        full_text = soup.get_text(" ", strip=False)

        for label, key in [("Užitná plocha", "uzitna_plocha"), ("Celková plocha", "celkova_plocha")]:
            m = re.search(rf"{label}\s*[\n\r\s]+([0-9][0-9\s,.]*)\s*m[²2]?", full_text, re.I)
            if m:
                raw = re.sub(r"[\s\xa0]", "", m.group(1)).replace(",", ".").replace(".", "", m.group(1).count(".") - 1 if "." in m.group(1) else 0)
                try:
                    # Clean number: remove spaces, handle Czech style "2.059" as 2059
                    clean = re.sub(r"[^\d]", "", m.group(1))
                    params[key] = float(clean) if clean else None
                except ValueError:
                    pass

        return params

    def _extract_description(self, soup: BeautifulSoup) -> str:
        """Get the main description paragraphs."""
        paragraphs = []
        for p in soup.select("p"):
            txt = p.get_text(" ", strip=True)
            if len(txt) > 80:
                paragraphs.append(txt)
        return "\n\n".join(paragraphs[:8]) if paragraphs else ""

    def _extract_photos(self, soup: BeautifulSoup) -> List[str]:
        """Extract full-size Webflow CDN photo URLs."""
        photos = []
        seen: set = set()
        for img in soup.select("img[src*='website-files.com']"):
            src = img.get("src", "").strip()
            if not src or src in seen:
                continue
            # Skip icons and tiny thumbnails (SVG icons, logos)
            if re.search(r"\.(svg)$", src, re.I):
                continue
            # Skip very small or icon-like images by their path
            if "icon" in src.lower() or "logo" in src.lower():
                continue
            seen.add(src)
            photos.append(src)
            if len(photos) >= 20:
                break
        return photos
