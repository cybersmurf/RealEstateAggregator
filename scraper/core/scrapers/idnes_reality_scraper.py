"""
Idnes Reality scraper for Czech real estate listings.

Strategy:
- Sitemap-based discovery (https://reality.idnes.cz/sitemap.xml)
- Detail pages only (SSR via httpx + BeautifulSoup)
- No Playwright needed (server-rendered HTML)
"""
import asyncio
import gzip
import logging
import re
import time
import xml.etree.ElementTree as ET
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..http_utils import http_retry

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)


class IdnesRealityScraper:
    """Scraper for reality.idnes.cz (Czech News Agency real estate portal)."""

    BASE_URL = "https://reality.idnes.cz"
    SITEMAP_URL = f"{BASE_URL}/sitemap.xml"
    SITEMAP_BASE = f"{BASE_URL}/sitemap/"
    # These sub-sitemaps contain individual listing detail pages
    # nemovitosti-hledani.xml.gz contains search/filter pages only
    LISTING_SITEMAPS = ["nemovitosti.xml.gz", "nemovitosti2.xml.gz", "nemovitosti3.xml.gz"]
    SITEMAP_NS = "http://www.sitemaps.org/schemas/sitemap/0.9"
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
                    # 🔥 Fetch listing URLs from gz sub-sitemaps
                    with timer("Fetch gz sitemaps"):
                        listing_urls = await self._fetch_all_listing_urls()

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

    async def _fetch_all_listing_urls(self) -> List[str]:
        """
        Fetch Znojmo listing URLs from IDNES gz sub-sitemaps.

        The main sitemap.xml is a sitemap index pointing to .gz sub-sitemaps.
        nemovitosti*.xml.gz files contain individual listing detail pages.
        Filter: URL must contain /detail/ AND 'znojmo' in path.

        Returns:
            List of detail page URLs for Znojmo area.
        """
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        ns = self.SITEMAP_NS
        urls: List[str] = []

        for sitemap_name in self.LISTING_SITEMAPS:
            sitemap_url = self.SITEMAP_BASE + sitemap_name
            try:
                logger.debug(f"Fetching gz sitemap: {sitemap_url}")
                response = await self._http_client.get(sitemap_url)
                response.raise_for_status()

                # Decompress gzip content
                xml_bytes = gzip.decompress(response.content)
                root = ET.fromstring(xml_bytes)

                batch_urls = [
                    loc_elem.text
                    for loc_elem in root.findall(f".//{{{ns}}}loc")
                    if loc_elem.text and "/detail/" in loc_elem.text and "znojmo" in loc_elem.text.lower()
                ]
                urls.extend(batch_urls)
                logger.info(f"Sitemap {sitemap_name}: {len(batch_urls)} Znojmo URLs")

            except Exception as exc:
                logger.error(f"Failed to process sitemap {sitemap_name}: {exc}")

        logger.info(f"Total Znojmo detail URLs found: {len(urls)}")
        return urls

    @http_retry
    async def _fetch_page(self, url: str) -> str:
        """Fetch detail page via HTTP. Opakuje při 429/503."""
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

            # Extract price - IDNES uses .b-detail__price
            price = None
            for sel in [".b-detail__price", ".cena", "[itemprop='price']"]:
                price_elem = soup.select_one(sel)
                if price_elem:
                    price_text = price_elem.get_text(strip=True)
                    # Match a plausible Czech price: 4-9 digits optionally separated by spaces/dots
                    # e.g. "1 500 000 Kč" or "2.500.000 Kč" or "950000 Kč"
                    price_match = re.search(r"\b(\d[\d\s.]{2,10}\d)\s*(Kč|CZK)", price_text)
                    if price_match:
                        digits = re.sub(r"[^\d]", "", price_match.group(1))
                        try:
                            val = int(digits)
                            # Sanity check: 10 000 – 500 000 000 Kč
                            if 10_000 <= val <= 500_000_000:
                                price = val
                        except ValueError:
                            pass
                    break

            # Extract location - try HTML first, fallback to URL slug
            # IDNES uses .b-detail__info-item or address elements
            location = None
            for sel in [
                ".b-detail__info .icoi-location",
                ".b-detail__info-item--location",
                "[itemprop='addressLocality']",
                ".b-detail__place",
            ]:
                elem = soup.select_one(sel)
                if elem:
                    location = elem.get_text(strip=True)
                    break

            # Fallback: extract location slug from URL path
            # URL format: /detail/{prodej|pronajem}/{type}/{location-slug}/{id}/
            if not location:
                url_parts = url.rstrip("/").split("/")
                # Parts: ['', 'detail', 'prodej', 'dum', 'znojmo-na-valech', 'ID']
                if len(url_parts) >= 5:
                    location_slug = url_parts[-2]
                    # Convert kebab-case slug to readable text
                    location = location_slug.replace("-", " ").title()

            location = location or "Znojmo"

            # Determine property type from URL (IDNES uses English slugs)
            # URL segments: /dum/, /byt/, /pozemek/, /chata/, /komercni-nemovitost/, etc.
            url_lower = url.lower()
            property_type = "Other"
            if "/byt/" in url_lower or "/byt-" in url_lower:
                property_type = "Apartment"
            elif "/dum/" in url_lower or "/dum-" in url_lower or "/domy/" in url_lower:
                property_type = "House"
            elif "/pozemek/" in url_lower or "/pozemek-" in url_lower:
                property_type = "Land"
            elif "/chata/" in url_lower or "/chalupa/" in url_lower or "/chata-" in url_lower:
                property_type = "Cottage"
            elif "/komercni" in url_lower or "/komerci" in url_lower:
                property_type = "Commercial"
            elif "/garaz/" in url_lower or "/garaz-" in url_lower:
                property_type = "Garage"

            # Offer type from URL
            offer_type = "Rent" if "/pronajem/" in url_lower else "Sale"

            # Extract photos - IDNES uses .photoSlider or .b-slider images
            photos = []
            for img in soup.select(".b-slider__item img, .photoSlider img, .gallery img"):
                src = img.get("src") or img.get("data-src") or img.get("data-lazy")
                if src and src.startswith("http"):
                    photos.append(src)
            # Also check og:image tags
            if not photos:
                for og in soup.find_all("meta", property="og:image"):
                    content = og.get("content")
                    if content:
                        photos.append(content)
            photos = list(dict.fromkeys(photos))[:20]  # deduplicate, max 20

            # Extract description - IDNES uses .b-detail__desc or .b-detail__text
            description = ""
            for sel in [".b-detail__desc", ".b-detail__text", "[itemprop='description']"]:
                elem = soup.select_one(sel)
                if elem:
                    description = elem.get_text(strip=True)
                    break

            # Extract area - look in table params or title
            area = None
            # Try to find in spec table (IDNES uses .b-detail__info table)
            for row in soup.select(".b-detail__info-item, .b-detail__param"):
                text = row.get_text(" ", strip=True)
                area_match = re.search(r"Plocha\D+?(\d+)\s*m", text, re.IGNORECASE)
                if area_match:
                    try:
                        area = int(area_match.group(1))
                        break
                    except ValueError:
                        pass
            # Fallback: extract area from title (e.g. "Prodej domu 120 m²")
            if not area:
                title_area = re.search(r"(\d+)\s*m[²2]", title)
                if title_area:
                    try:
                        area = int(title_area.group(1))
                    except ValueError:
                        pass

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
                "location_text": location[:200] if location else "Znojmo",
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

        URL format: https://reality.idnes.cz/detail/prodej/dum/znojmo/68f114793da2f02fc20a2b19/
        ID is last path segment (hexadecimal or numeric).
        """
        # Strip trailing slash, take last path segment
        return url.rstrip("/").split("/")[-1]

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
