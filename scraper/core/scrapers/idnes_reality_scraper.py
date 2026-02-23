"""
Idnes Reality scraper for Czech real estate listings.

Strategy:
- Sitemap-based discovery (https://reality.idnes.cz/sitemap.xml)
- Detail pages only (SSR via httpx + BeautifulSoup)
- No Playwright needed (server-rendered HTML)
"""
import asyncio
import logging
import re
import time
import xml.etree.ElementTree as ET
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)


class IdnesRealityScraper:
    """Scraper for reality.idnes.cz (Czech News Agency real estate portal)."""

    BASE_URL = "https://reality.idnes.cz"
    SITEMAP_URL = f"{BASE_URL}/sitemap.xml"
    SOURCE_CODE = "IDNES"

    def __init__(self):
        """Initialize the scraper."""
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """
        Main entry point called from runner.py.

        Args:
            full_rescan: If True, scrape all listings; otherwise limit to max_pages

        Returns:
            Number of successfully scraped listings
        """
        max_pages = 999 if full_rescan else 100
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 100) -> int:
        """
        Main scraping orchestrator.

        Args:
            max_pages: Maximum detail pages to process

        Returns:
            Number of scraped listings
        """
        logger.info(f"Starting Idnes Reality scraper (max_pages={max_pages})")

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
            ) as client:
                self._http_client = client

                try:
                    # 🔥 Fetch and parse sitemap
                    with timer("Fetch sitemap"):
                        sitemap_xml = await self._fetch_sitemap()

                    with timer("Parse sitemap"):
                        listing_urls = self._extract_listing_urls(sitemap_xml)

                    if not listing_urls:
                        logger.warning("No listings found in sitemap")
                        return 0

                    logger.info(f"Found {len(listing_urls)} listings in sitemap")

                    # 🔥 Process detail pages
                    count = 0
                    for idx, listing_url in enumerate(listing_urls[:max_pages]):
                        try:
                            with timer(f"Fetch detail {idx + 1}/{min(len(listing_urls), max_pages)}"):
                                detail_html = await self._fetch_page(listing_url)

                            normalized = self._parse_detail_page(detail_html, listing_url)

                            if normalized:
                                await self._save_listing(normalized)
                                count += 1
                                metrics.increment_scraped()

                        except Exception as exc:
                            logger.error(f"Error processing listing {listing_url}: {exc}")
                            metrics.increment_failed()

                        # Throttling
                        await asyncio.sleep(0.5)

                    self.scraped_count = count

                except Exception as exc:
                    logger.error(f"Scraping failed: {exc}")
                    metrics.increment_failed()

                finally:
                    self._http_client = None

        logger.info(f"Idnes Reality scraper finished. Scraped {self.scraped_count} listings")
        return self.scraped_count

    async def _fetch_sitemap(self) -> str:
        """Fetch and parse sitemap XML."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        logger.debug(f"Fetching sitemap: {self.SITEMAP_URL}")
        response = await self._http_client.get(self.SITEMAP_URL)
        response.raise_for_status()
        return response.text

    def _extract_listing_urls(self, sitemap_xml: str) -> List[str]:
        """
        Extract listing URLs from sitemap XML.

        Sitemap contains URLs like:
        https://reality.idnes.cz/prodej/byt/praha/1234567
        """
        try:
            root = ET.fromstring(sitemap_xml)

            # Handle XML namespaces
            namespaces = {"": "http://www.sitemaps.org/schemas/sitemap/0.9"}
            urls = []

            for url_elem in root.findall(".//{http://www.sitemaps.org/schemas/sitemap/0.9}loc"):
                url = url_elem.text
                if url:
                    # Filter for listing URLs (prodej/byt, prodej/dům, etc.) + only Znojmo region
                    if ("/prodej/" in url or "/pronajem/" in url) and "znojmo" in url.lower():
                        urls.append(url)

            return urls

        except Exception as exc:
            logger.error(f"Failed to parse sitemap: {exc}")
            return []

    async def _fetch_page(self, url: str) -> str:
        """Fetch detail page via HTTP."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        logger.debug(f"Fetching: {url}")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_detail_page(self, html: str, url: str) -> Optional[Dict[str, Any]]:
        """
        Parse detail page HTML.

        Extracts:
        - Title
        - Price
        - Location
        - Property type
        - Offer type
        - Photos
        - Description
        - Area (if available)
        """
        soup = BeautifulSoup(html, "html.parser")

        try:
            # Extract title
            title_elem = soup.find("h1", class_=re.compile("title|heading|main-title"))
            if not title_elem:
                title_elem = soup.select_one("h1")
            title = title_elem.get_text(strip=True) if title_elem else "N/A"

            # Extract price
            price_elem = soup.find("span", class_=re.compile("price"))
            price = None
            if price_elem:
                price_text = price_elem.get_text(strip=True)
                price_match = re.search(r"(\d+[\s.]?)+(Kč|CZK)?", price_text)
                if price_match:
                    digits = re.sub(r"\D", "", price_match.group(0))
                    try:
                        price = int(digits) if digits else None
                    except ValueError:
                        price = None

            # Extract location
            location_elem = soup.find("span", class_=re.compile("location|place|address"))
            if not location_elem:
                location_elem = soup.select_one("[itemprop='addressLocality']")
            location = location_elem.get_text(strip=True) if location_elem else "N/A"

            # Determine property type from URL
            property_type = "Ostatni"
            offer_type = "Sale"

            if "/byt" in url.lower():
                property_type = "Apartment"
            elif "/dům" in url.lower() or "/dum" in url.lower():
                property_type = "House"
            elif "/pozemek" in url.lower():
                property_type = "Land"

            if "/pronajem" in url.lower():
                offer_type = "Rent"
            else:
                offer_type = "Sale"

            # Extract photos
            photos = []
            for img in soup.find_all("img", class_=re.compile("photo|image|gallery")):
                src = img.get("src") or img.get("data-src")
                if src:
                    full_url = urljoin(self.BASE_URL, src)
                    photos.append(full_url)

            # Extract description
            desc_elem = soup.find("div", class_=re.compile("description|details|text"))
            if not desc_elem:
                desc_elem = soup.select_one("[itemprop='description']")
            description = desc_elem.get_text(strip=True) if desc_elem else ""

            # Extract area (optional)
            area = None
            area_text = soup.find("span", class_=re.compile("area|size"))
            if area_text:
                area_match = re.search(r"(\d+)\s*m", area_text.get_text())
                if area_match:
                    try:
                        area = int(area_match.group(1))
                    except ValueError:
                        area = None

            # Return normalized data
            return {
                "source_code": self.SOURCE_CODE,
                "external_id": self._extract_external_id(url),
                "url": url,
                "title": title[:200],
                "description": description[:5000],
                "property_type": property_type,
                "offer_type": offer_type,
                "price": price,
                "location_text": location[:200],
                "photos": photos[:20],
                "area_built_up": area,
            }

        except Exception as exc:
            logger.error(f"Error parsing detail page {url}: {exc}")
            return None

    @staticmethod
    def _extract_external_id(url: str) -> str:
        """
        Extract external ID from URL.

        URL format: https://reality.idnes.cz/prodej/byt/praha/1234567
        ID is last numeric part
        """
        match = re.search(r"(\d+)/?$", url)
        if match:
            return match.group(1)
        return url.split("/")[-1]

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        """Save listing to database."""
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kč",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
