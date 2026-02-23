"""
DeluXreality scraper – deluxreality.cz
Realitní kancelář Znojmo (Delux services s.r.o.)
WordPress / Elementor SSR – httpx + BeautifulSoup
"""
import logging
import re
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://deluxreality.cz"
LISTINGS_URL = f"{BASE_URL}/nemovitosti/"

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
    "apartmán": "Apartment",
    "dům": "House",
    "dom": "House",
    "rodinný": "House",
    "vila": "House",
    "pozemek": "Land",
    "parcela": "Land",
    "zahrada": "Land",
    "stavební": "Land",
    "komerční": "Commercial",
    "sklady": "Commercial",
    "kanceláře": "Commercial",
    "chata": "Cottage",
    "chalupa": "Cottage",
    "rekreace": "Cottage",
    "horský": "Cottage",
}


class DeluxRealityScraper:
    SOURCE_CODE = "DELUXREALITY"

    async def run(self, full_rescan: bool = False) -> int:
        """Run the scraper and return count of processed listings."""
        logger.info(f"[{self.SOURCE_CODE}] Starting scrape (full_rescan={full_rescan})")
        try:
            return await self.scrape()
        except Exception as e:
            logger.error(f"[{self.SOURCE_CODE}] Fatal error: {e}", exc_info=True)
            return 0

    async def scrape(self) -> int:
        """Fetch listing page, then detail pages, persist to DB."""
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

    @http_retry
    async def _fetch(self, client: httpx.AsyncClient, url: str) -> str:
        """Stahne stránku, při 429/503 automaticky opakuje (max 3×)."""
        resp = await client.get(url)
        resp.raise_for_status()
        return resp.text

    async def _get_listing_urls(self, client: httpx.AsyncClient) -> List[str]:
        """Scrape the main listings page and return unique detail URLs."""
        html = await self._fetch(client, LISTINGS_URL)
        soup = BeautifulSoup(html, "html.parser")

        urls = []
        seen = set()
        for a in soup.select("a[href*='/nemovitost/']"):
            href = a.get("href", "").strip()
            if not href:
                continue
            full_url = urljoin(BASE_URL, href)
            # Skip anchor-only / query-string variants
            if full_url in seen or full_url.endswith("/nemovitosti/"):
                continue
            seen.add(full_url)
            urls.append(full_url)

        return urls

    async def _parse_detail(
        self, client: httpx.AsyncClient, url: str
    ) -> Optional[Dict[str, Any]]:
        """Fetch and parse a single property detail page."""
        html = await self._fetch(client, url)
        soup = BeautifulSoup(html, "html.parser")

        # External ID = URL slug after /nemovitost/
        slug_match = re.search(r"/nemovitost/([^/]+)/?$", url)
        external_id = slug_match.group(1) if slug_match else url

        # Title
        h1 = soup.find("h1")
        title = h1.get_text(strip=True) if h1 else ""
        if not title:
            logger.warning(f"[{self.SOURCE_CODE}] No title at {url}")
            return None

        # Offer type (Prodej / Pronájem) – from H2 subtitle or H1
        offer_type = self._detect_offer_type(soup, title)

        # Property type – from H1 / H2
        property_type = self._detect_property_type(soup, title)

        # Price
        price = self._extract_price(soup)

        # Area
        area = self._extract_area(soup)

        # Location
        location = self._extract_location(soup)

        # Description
        description = self._extract_description(soup)

        # Photos – empty <a> tags linking to full-size images in wp-content/uploads
        photos = self._extract_photos(soup, url)

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

    def _detect_offer_type(self, soup: BeautifulSoup, title: str) -> str:
        text = (title + " " + (soup.find("h2").get_text(" ", strip=True) if soup.find("h2") else "")).lower()
        for keyword, otype in OFFER_TYPE_MAP.items():
            if keyword in text:
                return otype
        return "Sale"

    def _detect_property_type(self, soup: BeautifulSoup, title: str) -> str:
        h2 = soup.find("h2")
        text = (title + " " + (h2.get_text(" ", strip=True) if h2 else "")).lower()
        for keyword, ptype in PROPERTY_TYPE_MAP.items():
            if keyword in text:
                return ptype
        return "Other"

    def _extract_price(self, soup: BeautifulSoup) -> Optional[float]:
        """Find the primary price (Kč amount) on the page."""
        # Look for the price element that contains Kč, prefer the main offer price
        # It appears near "MÁM ZÁJEM O NEMOVITOST" button or in "### Cena" section
        for el in soup.find_all(string=re.compile(r"\d[\d\s]+Kč")):
            raw = el.strip()
            # Skip long strings (descriptions), grab clean price strings
            if len(raw) > 60:
                continue
            # Parse digits
            nums = re.sub(r"[^\d]", "", raw)
            if nums and int(nums) > 10000:
                return float(int(nums))
        return None

    def _extract_area(self, soup: BeautifulSoup) -> Optional[float]:
        """Extract usable area in m²."""
        # Strategy 1: look for "Plocha" header followed by numeric value
        for heading in soup.find_all(re.compile(r"^h\d$"), string=re.compile(r"Plocha", re.I)):
            nxt = heading.find_next(string=re.compile(r"\d+\s*m"))
            if nxt:
                m = re.search(r"(\d+(?:[.,]\d+)?)", nxt)
                if m:
                    return float(m.group(1).replace(",", "."))

        # Strategy 2: bullet list item "plocha bytu: XX m²"
        for el in soup.find_all(string=re.compile(r"plocha\s+bytu|užitná\s+plocha|plocha\s+domu", re.I)):
            m = re.search(r"(\d+(?:[.,]\d+)?)\s*m", el, re.I)
            if m:
                return float(m.group(1).replace(",", "."))

        # Strategy 3: any text matching standalone "XX m²" pattern
        for el in soup.find_all(string=re.compile(r"\b\d{2,4}\s*m[²2]")):
            m = re.search(r"(\d+(?:[.,]\d+)?)\s*m[²2]", el)
            if m:
                val = float(m.group(1).replace(",", "."))
                if 10 < val < 2000:
                    return val
        return None

    def _extract_location(self, soup: BeautifulSoup) -> str:
        """Extract location string from the page."""
        # Look for address-like text near broker/contact info
        # Pattern: "Tovární 16 Znojmo" appears in footer-style block
        for el in soup.find_all(string=re.compile(r"Znojmo|Hevlín|Šatov|Vrbovec|Mikulovice|Hnanice", re.I)):
            txt = el.strip()
            if 3 < len(txt) < 80:
                return txt
        return "Znojmo"

    def _extract_description(self, soup: BeautifulSoup) -> str:
        """Get the main property description text."""
        # Find the article / main content paragraphs
        paragraphs = []
        for p in soup.select("p"):
            txt = p.get_text(" ", strip=True)
            if len(txt) > 80:
                paragraphs.append(txt)
        return "\n\n".join(paragraphs[:6]) if paragraphs else ""

    def _extract_photos(self, soup: BeautifulSoup, base_url: str) -> List[str]:
        """Extract full-size photo URLs from empty <a> links to wp-content/uploads."""
        photos = []
        seen = set()
        # Pattern: <a href="https://deluxreality.cz/2019/wp-content/uploads/...jpg">
        for a in soup.select("a[href*='wp-content/uploads']"):
            href = a.get("href", "").strip()
            if not href:
                continue
            if not re.search(r"\.(jpg|jpeg|png|webp)$", href, re.I):
                continue
            # Skip thumbnails (already have full-size from empty <a> links)
            if href not in seen:
                seen.add(href)
                photos.append(href)
            if len(photos) >= 20:
                break
        return photos
