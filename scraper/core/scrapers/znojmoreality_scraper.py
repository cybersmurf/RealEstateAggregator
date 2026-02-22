"""
Znojmo Reality scraper (znojmoreality.cz).

Realman platform, SSR pages. No pagination.
Listing URLs:
- https://www.znojmoreality.cz/domy
- https://www.znojmoreality.cz/byty
- https://www.znojmoreality.cz/pozemky
- https://www.znojmoreality.cz/ostatni

Detail URL pattern: /{slug}-{id}
"""
import asyncio
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin, urlparse

import httpx
from bs4 import BeautifulSoup

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.znojmoreality.cz"

LISTING_URLS = [
    {"url": f"{BASE_URL}/domy", "property_type": "Dům"},
    {"url": f"{BASE_URL}/byty", "property_type": "Byt"},
    {"url": f"{BASE_URL}/pozemky", "property_type": "Pozemek"},
    {"url": f"{BASE_URL}/ostatni", "property_type": "Komerční"},
]

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9",
}


class ZnojmoRealityScraper:
    """Scraper for Znojmo Reality (Realman platform)."""

    SOURCE_CODE = "ZNOJMOREALITY"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """Entry point from runner.py. No pagination, full_rescan has no effect."""
        return await self.scrape()

    async def scrape(self) -> int:
        logger.info("Starting Znojmo Reality scraper")

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                for config in LISTING_URLS:
                    try:
                        count = await self._scrape_listing(config, metrics)
                        self.scraped_count += count
                    except Exception as exc:
                        logger.error("Error scraping %s: %s", config["url"], exc)
                        metrics.increment_failed()

                self._http_client = None

        logger.info("Znojmo Reality scraper finished. Scraped %s listings", self.scraped_count)
        return self.scraped_count

    async def _scrape_listing(self, config: Dict[str, Any], metrics: Any) -> int:
        scraped = 0

        with timer(f"Fetch listing {config['url']}"):
            start = time.perf_counter()
            html = await self._fetch(config["url"])
            metrics.record_fetch(time.perf_counter() - start)

        items = self._parse_listing(html, config)
        logger.info("Found %s listings at %s", len(items), config["url"])

        for item in items:
            try:
                detail_html = await self._fetch(item["detail_url"])
                normalized = self._parse_detail(detail_html, item)
                await self._save_listing(normalized)
                scraped += 1
                metrics.increment_scraped()
                await asyncio.sleep(0.5)
            except Exception as exc:
                logger.error("Error processing %s: %s", item.get("detail_url"), exc)
                metrics.increment_failed()

        return scraped

    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_listing(self, html: str, config: Dict[str, Any]) -> List[Dict[str, Any]]:
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen_ids: set[str] = set()

        for link in soup.find_all("a", href=True):
            href = link.get("href", "").strip()
            if not href or href.startswith("#"):
                continue

            full_url = urljoin(BASE_URL, href)
            parsed = urlparse(full_url)
            if parsed.netloc and parsed.netloc != urlparse(BASE_URL).netloc:
                continue

            path = parsed.path.rstrip("/")
            id_match = re.search(r"-(\d+)$", path)
            if not id_match:
                continue

            external_id = id_match.group(1)
            if external_id in seen_ids:
                continue
            seen_ids.add(external_id)

            title = ""
            parent = link.find_parent()
            if parent:
                heading = parent.find(["h1", "h2", "h3"])
                if heading:
                    title = heading.get_text(" ", strip=True)
            if not title:
                title = link.get_text(" ", strip=True)

            price_text = self._extract_price_from_context(link)

            results.append(
                {
                    "source_code": self.SOURCE_CODE,
                    "external_id": external_id,
                    "detail_url": full_url,
                    "title": title[:200],
                    "price_text": price_text,
                    "property_type": config.get("property_type", "Ostatní"),
                }
            )

        logger.debug("Parsed %s unique items from listing", len(results))
        return results

    def _extract_price_from_context(self, link_el: Any) -> str:
        parent = link_el.find_parent()
        if parent:
            text = parent.get_text(" ", strip=True)
            match = re.search(r"([\d\s]+)\s*K[cč]", text, re.IGNORECASE)
            if match:
                return match.group(0).strip()
        return ""

    def _parse_detail(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": list_item["external_id"],
            "url": list_item["detail_url"],
            "property_type": list_item.get("property_type", "Ostatní"),
        }

        h1 = soup.find("h1")
        result["title"] = h1.get_text(" ", strip=True)[:200] if h1 else list_item.get("title", "")

        title_lower = result["title"].lower()
        result["offer_type"] = "Pronájem" if "pronajem" in title_lower or "pronájem" in title_lower else "Prodej"

        params = self._parse_params_table(soup)
        result["price"] = self._parse_price(params.get("Cena", list_item.get("price_text", "")))
        result["area_built_up"] = self._parse_area(params.get("Užitná plocha", ""))
        result["area_land"] = self._parse_area(params.get("Plocha pozemku", ""))

        locality = params.get("Lokalita", "").strip()
        district = params.get("Okres", "").strip()
        result["location_text"] = locality or district

        result["description"] = self._extract_description(soup)
        result["photos"] = self._extract_photos(soup)

        lat, lng = self._extract_gps(html)
        if lat is not None and lng is not None:
            result["latitude"] = lat
            result["longitude"] = lng

        return result

    def _parse_params_table(self, soup: BeautifulSoup) -> Dict[str, str]:
        params: Dict[str, str] = {}
        for table in soup.find_all("table"):
            for row in table.find_all("tr"):
                cells = row.find_all("td")
                if len(cells) < 2:
                    continue
                key = cells[0].get_text(" ", strip=True)
                val = cells[1].get_text(" ", strip=True)
                if key:
                    params[self._normalize_label(key)] = val
        return params

    def _normalize_label(self, label: str) -> str:
        cleaned = label.strip().lower()
        cleaned = cleaned.replace(":", "")
        map_labels = {
            "cena": "Cena",
            "lokalita": "Lokalita",
            "okres": "Okres",
            "užitná plocha": "Užitná plocha",
            "plocha pozemku": "Plocha pozemku",
        }
        return map_labels.get(cleaned, label.strip())

    def _extract_description(self, soup: BeautifulSoup) -> str:
        desc = soup.select_one(".description, .popis, [class*='description']")
        if desc:
            return desc.get_text(" ", strip=True)[:5000]

        for table in soup.find_all("table"):
            next_el = table.find_next_sibling()
            if next_el:
                text = next_el.get_text(" ", strip=True)
                if len(text) > 50:
                    return text[:5000]
        return ""

    def _extract_photos(self, soup: BeautifulSoup) -> List[str]:
        urls: List[str] = []
        for link in soup.select("a[href*='t.rmcl.cz']"):
            href = link.get("href")
            if href and href not in urls:
                urls.append(href)
        return urls[:20]

    def _extract_gps(self, html: str) -> Tuple[Optional[float], Optional[float]]:
        match = re.search(r"(?:L\.marker|setView)\(\[([0-9.]+),\s*([0-9.]+)\]", html)
        if match:
            return float(match.group(1)), float(match.group(2))

        lat_match = re.search(r"data-lat=\"([0-9.]+)\"", html)
        lng_match = re.search(r"data-lng=\"([0-9.]+)\"", html)
        if lat_match and lng_match:
            return float(lat_match.group(1)), float(lng_match.group(1))

        return None, None

    @staticmethod
    def _parse_price(text: str) -> Optional[int]:
        digits = "".join(c for c in text if c.isdigit())
        return int(digits) if digits else None

    @staticmethod
    def _parse_area(text: str) -> Optional[int]:
        digits = "".join(c for c in text if c.isdigit())
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
