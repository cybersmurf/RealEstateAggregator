"""
MM Reality scraper for Czech real estate listings.

Strategie:
- httpx + BeautifulSoup pro list pages (SSR, bez JS)
- Detail pages SSR – httpx staci
- Fotky: UUID soubory z HTML → detekce CDN prefixu
- Parametry: dt/dd nebo div/span pary v sekci "Zakladni parametry"
"""
import asyncio
import html as html_lib
import json
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)


# Konfigurace search URL pro ruzne lokality (default - Znojmo)
# 🔥 Można být přepsáno z settings.yaml v runner.py
DEFAULT_SEARCH_CONFIGS = [
    {
        "url": "https://www.mmreality.cz/nemovitosti/prodej/domy/znojmo/",
        "offer_type": "Prodej",
        "property_type": "Dům",
    },
]

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9,en;q=0.8",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}


class MmRealityScraper:
    """Scraper pro MM Reality (mmreality.cz)."""

    BASE_URL = "https://www.mmreality.cz"
    SOURCE_CODE = "MMR"

    def __init__(self, search_configs: Optional[List[Dict[str, Any]]] = None):
        # 🔥 Use provided search_configs or fall back to defaults
        self.search_configs = search_configs or DEFAULT_SEARCH_CONFIGS
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """
        Hlavni entry point volany z runner.py.

        Args:
            full_rescan: Pokud True, scrapuje vsechny stranky, jinak jen prvnich 5
        Returns:
            Pocet uspesne scrapnutych inzeratu
        """
        max_pages = 100 if full_rescan else 5
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 5) -> int:
        """Scrapuje vsechny nakonfigurovane search URL."""
        logger.info("Starting MM Reality scraper (max_pages=%s)", max_pages)

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                for config in self.search_configs:
                    try:
                        count = await self._scrape_search(config, max_pages, metrics)
                        self.scraped_count += count
                    except Exception as exc:
                        logger.error("Error scraping config %s: %s", config.get("url"), exc)
                        metrics.increment_failed()

                self._http_client = None

        logger.info("MM Reality scraper finished. Scraped %s listings", self.scraped_count)
        return self.scraped_count

    async def _scrape_search(self, config: Dict[str, Any], max_pages: int, metrics: Any) -> int:
        """Scrapuje jednu search URL (vsechny stranky paginace)."""
        base_url = config["url"]
        scraped = 0
        page = 1

        while page <= max_pages:
            url = f"{base_url}?page={page}" if page > 1 else base_url

            try:
                with timer(f"Fetch list page {page} ({base_url})"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)

                with timer(f"Parse list page {page}"):
                    start = time.perf_counter()
                    items, has_next = self._parse_list_page(html)
                    metrics.record_parse(time.perf_counter() - start)

                if not items:
                    logger.info("No items on page %s, stopping", page)
                    break

                logger.info("Page %s: found %s listings", page, len(items))

                for item in items:
                    try:
                        detail_url = item["detail_url"]
                        detail_html = await self._fetch(detail_url)
                        normalized = self._parse_detail_page(detail_html, item, config)
                        await self._save_listing(normalized)
                        scraped += 1
                        metrics.increment_scraped()
                        await asyncio.sleep(0.5)
                    except Exception as exc:
                        logger.error("Error processing %s: %s", item.get("detail_url"), exc)
                        metrics.increment_failed()

                if not has_next:
                    logger.info("No next page after %s, stopping", page)
                    break

                page += 1
                await asyncio.sleep(1.0)

            except httpx.HTTPStatusError as exc:
                logger.error("HTTP error on page %s: %s", page, exc)
                break
            except Exception as exc:
                logger.error("Error on page %s: %s", page, exc)
                metrics.increment_failed()
                break

        return scraped

    async def _fetch(self, url: str) -> str:
        """Stahne HTML stranky pomoci httpx."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], bool]:
        """Parsuje listing stranku."""
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        ssr_data = None
        offers_grid = soup.select_one("vue-property-list-grid")
        if offers_grid:
            ssr_payload = offers_grid.get(":ssr") or offers_grid.get("v-bind:ssr")
            if ssr_payload:
                try:
                    ssr_data = json.loads(html_lib.unescape(ssr_payload))
                except json.JSONDecodeError as exc:
                    logger.warning("Failed to parse SSR payload: %s", exc)

        if ssr_data and ssr_data.get("offers"):
            for offer in ssr_data.get("offers", []):
                external_id = str(offer.get("id") or "").strip()
                if not external_id.isdigit():
                    continue

                detail_url = urljoin(self.BASE_URL, f"/nemovitosti/{external_id}")
                title = (offer.get("title") or offer.get("originalTitle") or "").strip()
                location = (
                    offer.get("location")
                    or offer.get("municipality")
                    or offer.get("district")
                    or ""
                )

                results.append(
                    {
                        "source_code": self.SOURCE_CODE,
                        "external_id": external_id,
                        "detail_url": detail_url,
                        "title": title[:200],
                        "price_text": "",
                        "img_alt": location,
                    }
                )
        else:
            offers_list = soup.select_one("#offers-list")
            if not offers_list:
                logger.warning("Element #offers-list not found on page")
                return [], False

            for card in offers_list.select("a[href]"):
                href = card.get("href", "")
                if not re.match(r"^/nemovitosti/\d+/?$", href):
                    continue

                match = re.search(r"/nemovitosti/(\d+)", href)
                if not match:
                    continue

                external_id = match.group(1)
                detail_url = urljoin(self.BASE_URL, href)

                title_el = card.find(["h4", "h3", "h6"])
                title = title_el.get_text(strip=True) if title_el else ""

                price_text = ""
                for el in card.find_all(string=re.compile(r"Kc|Kč")):
                    stripped = el.strip()
                    if re.search(r"\d", stripped) and len(stripped) < 40:
                        price_text = stripped
                        break

                first_img = card.find("img")
                img_alt = first_img.get("alt", "") if first_img else ""

                results.append(
                    {
                        "source_code": self.SOURCE_CODE,
                        "external_id": external_id,
                        "detail_url": detail_url,
                        "title": title[:200],
                        "price_text": price_text,
                        "img_alt": img_alt,
                    }
                )

        has_next = self._detect_next_page(soup)
        if not has_next and ssr_data:
            total = ssr_data.get("metadata", {}).get("count")
            page = ssr_data.get("page") or 1
            try:
                page = int(page)
            except (TypeError, ValueError):
                page = 1
            page_size = len(ssr_data.get("offers", []))
            if isinstance(total, int) and page_size and total > page * page_size:
                has_next = True

        seen: set = set()
        unique = []
        for item in results:
            if item["external_id"] not in seen:
                seen.add(item["external_id"])
                unique.append(item)

        logger.debug("Parsed %s unique listings from page", len(unique))
        return unique, has_next

    def _detect_next_page(self, soup: BeautifulSoup) -> bool:
        """Detekuje pritomnost dalsi stranky paginace."""
        pagination_links = soup.select(
            "nav[aria-label*='pagination'] a, [class*='pagination'] a, [class*='pager'] a"
        )
        for link in pagination_links:
            text = link.get_text(strip=True)
            href = link.get("href", "")
            if text in ("›", "»", "Další", ">") or "page=" in href:
                if "disabled" not in link.get("class", []):
                    return True

        page_numbers = []
        for el in soup.select("[aria-current='page'], [class*='active']"):
            try:
                page_numbers.append(int(el.get_text(strip=True)))
            except ValueError:
                pass

        if page_numbers:
            current = max(page_numbers)
            for el in soup.select("a[href*='page=']"):
                match = re.search(r"page=(\d+)", el.get("href", ""))
                if match and int(match.group(1)) > current:
                    return True

        return False

    def _parse_detail_page(self, html: str, list_item: Dict[str, Any], config: Dict[str, Any]) -> Dict[str, Any]:
        """Parsuje detail stranku inzeratu."""
        soup = BeautifulSoup(html, "html.parser")

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": list_item["external_id"],
            "url": list_item["detail_url"],
            "offer_type": config.get("offer_type", "Prodej"),
            "property_type": config.get("property_type", "Ostatní"),
        }

        title_el = soup.find("h1") or soup.find("h2")
        result["title"] = (
            title_el.get_text(" ", strip=True)[:200] if title_el else list_item.get("title", "")
        )

        title_lower = result["title"].lower()
        if not result["property_type"] or result["property_type"] == "Ostatní":
            if "byt" in title_lower:
                result["property_type"] = "Byt"
            elif "pozemek" in title_lower:
                result["property_type"] = "Pozemek"
            elif "dum" in title_lower or "dům" in title_lower or "vila" in title_lower:
                result["property_type"] = "Dům"

        if "pronajem" in title_lower or "pronájem" in title_lower:
            result["offer_type"] = "Pronájem"

        price_text = ""
        for el in soup.find_all(string=lambda t: t and "Kč" in t):
            stripped = el.strip()
            if re.search(r"\d", stripped) and len(stripped) < 40:
                price_text = stripped
                break
        result["price"] = self._parse_price(price_text)

        desc_el = soup.select_one(".description p, article p, main p")
        result["description"] = desc_el.get_text(" ", strip=True) if desc_el else ""

        params = self._parse_params_section(soup)
        if not params:
            params = self._parse_params_fallback(soup)

        result["area_built_up"] = self._parse_area(params.get("Užitná plocha", ""))
        result["area_land"] = self._parse_area(params.get("Plocha parcely", ""))
        result["location_text"] = self._extract_location(soup) or list_item.get("img_alt", "")

        result["photos"] = self._extract_photos(soup, html)[:20]

        lat, lng = self._extract_coordinates(html)
        if lat is not None and lng is not None:
            result["latitude"] = lat
            result["longitude"] = lng

        return result

    def _parse_params_section(self, soup: BeautifulSoup) -> Dict[str, str]:
        params: Dict[str, str] = {}
        for heading in soup.find_all(["h3", "h4"]):
            if "parametr" not in heading.get_text(strip=True).lower():
                continue

            sibling = heading.find_next_sibling()
            while sibling:
                labels = sibling.find_all("dt") or sibling.find_all("th")
                values = sibling.find_all("dd") or sibling.find_all("td")
                for label, value in zip(labels, values):
                    key = label.get_text(" ", strip=True)
                    val = value.get_text(" ", strip=True)
                    if key:
                        params[key] = val
                sibling = sibling.find_next_sibling()
                if sibling and sibling.name in ["h3", "h4"]:
                    break
        return params

    def _parse_params_fallback(self, soup: BeautifulSoup) -> Dict[str, str]:
        params: Dict[str, str] = {}
        pairs = soup.select("[data-label][data-value]")
        for pair in pairs:
            key = pair.get("data-label", "").strip()
            val = pair.get("data-value", "").strip()
            if key:
                params[key] = val
        return params

    def _extract_location(self, soup: BeautifulSoup) -> str:
        breadcrumb = soup.select("nav[aria-label='breadcrumb'] a, ol.breadcrumb a")
        if breadcrumb:
            return breadcrumb[-1].get_text(strip=True)
        return ""

    def _extract_photos(self, soup: BeautifulSoup, html: str) -> List[str]:
        pattern = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.jpe?g"
        filenames = list(dict.fromkeys(re.findall(pattern, html)))

        cdn_base = "https://www.mmreality.cz/media/"
        img_tags = soup.find_all("img", src=True)
        if filenames:
            for img in img_tags:
                src = img.get("src", "")
                if filenames[0] in src:
                    idx = src.find(filenames[0])
                    if idx > 0:
                        cdn_base = src[:idx]
                    break

        return [cdn_base + f for f in filenames]

    def _extract_coordinates(self, html: str) -> Tuple[Optional[float], Optional[float]]:
        match = re.search(r"L\.marker\(\[([0-9.]+),\s*([0-9.]+)\]\)", html)
        if match:
            return float(match.group(1)), float(match.group(2))
        match = re.search(r"setView\(\[([0-9.]+),\s*([0-9.]+)\]", html)
        if match:
            return float(match.group(1)), float(match.group(2))
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
        try:
            db = get_db_manager()
            listing_id = await db.upsert_listing(listing)
            logger.info(
                "Saved listing %s: %s | %s Kč",
                listing_id,
                listing.get("title", "N/A")[:50],
                listing.get("price", "N/A"),
            )
        except Exception as exc:
            logger.error("Failed to save listing: %s", exc)
            raise
