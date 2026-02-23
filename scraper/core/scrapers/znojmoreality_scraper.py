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
import json
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

        cards = soup.select(".polozka")
        if cards:
            for card in cards:
                link = card.find("a", href=True)
                if not link:
                    continue

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
                heading = card.find(["h1", "h2", "h3"])
                if heading:
                    title = heading.get_text(" ", strip=True)
                if not title:
                    title = link.get_text(" ", strip=True)

                price_text = self._extract_price_from_context(card)

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

            logger.debug("Parsed %s unique items from card listing", len(results))
            return results

        for link in soup.find_all("a", href=True):
            href = link.get("href", "").strip()
            if not href or href.startswith("#"):
                continue

            href_lower = href.lower()
            if "prodej" not in href_lower and "pronajem" not in href_lower and "pronájem" not in href_lower:
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
        json_ld = self._extract_json_ld(soup)

        result["price"] = self._parse_price(
            params.get("Cena", "") or list_item.get("price_text", "")
        )
        if result.get("price") is None:
            result["price"] = self._parse_price(self._extract_price_from_json_ld(json_ld))
        if result.get("price") is None:
            result["price"] = self._parse_price(self._extract_price_from_text(soup))

        result["area_built_up"] = self._parse_area(params.get("Užitná plocha", ""))
        result["area_land"] = self._parse_area(params.get("Plocha pozemku", ""))

        locality = params.get("Lokalita", "").strip()
        district = params.get("Okres", "").strip()
        location_from_ld = self._extract_location_from_json_ld(json_ld)
        location_from_breadcrumbs = self._extract_location_from_breadcrumbs(soup)
        result["location_text"] = locality or location_from_ld or location_from_breadcrumbs or district

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

    def _extract_json_ld(self, soup: BeautifulSoup) -> List[Dict[str, Any]]:
        payloads: List[Dict[str, Any]] = []
        for script in soup.find_all("script", type="application/ld+json"):
            raw = (script.string or "").strip()
            if not raw:
                continue
            try:
                data = json.loads(raw)
            except json.JSONDecodeError:
                continue

            if isinstance(data, list):
                payloads.extend(item for item in data if isinstance(item, dict))
            elif isinstance(data, dict):
                payloads.append(data)

        return payloads

    def _extract_price_from_json_ld(self, payloads: List[Dict[str, Any]]) -> str:
        for payload in payloads:
            offers = payload.get("offers")
            if isinstance(offers, dict):
                price = offers.get("price") or offers.get("priceSpecification", {}).get("price")
                if price is not None:
                    return str(price)
            elif isinstance(offers, list):
                for offer in offers:
                    if not isinstance(offer, dict):
                        continue
                    price = offer.get("price") or offer.get("priceSpecification", {}).get("price")
                    if price is not None:
                        return str(price)
        return ""

    def _extract_location_from_json_ld(self, payloads: List[Dict[str, Any]]) -> str:
        for payload in payloads:
            address = payload.get("address") or payload.get("location")
            if isinstance(address, dict):
                parts = [
                    address.get("streetAddress"),
                    address.get("addressLocality"),
                    address.get("addressRegion"),
                ]
                location = ", ".join(part for part in parts if part)
                if location:
                    return location
        return ""

    def _extract_location_from_breadcrumbs(self, soup: BeautifulSoup) -> str:
        crumbs: List[str] = []
        for nav in soup.select("nav, .breadcrumb, .breadcrumbs"):
            for el in nav.find_all(["a", "span", "li"]):
                text = el.get_text(" ", strip=True)
                if text and text not in crumbs:
                    crumbs.append(text)
            if crumbs:
                break
        if len(crumbs) >= 2:
            return " - ".join(crumbs[-2:])
        return ""

    def _extract_price_from_text(self, soup: BeautifulSoup) -> str:
        text = soup.get_text(" ", strip=True)
        match = re.search(r"([\d\s]+)\s*K[cč]", text, re.IGNORECASE)
        return match.group(0).strip() if match else ""

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
        # isdigit() vrací True i pro unicode znaky jako '²' – použij re.sub
        digits = re.sub(r"[^0-9]", "", text)
        return int(digits) if digits else None

    @staticmethod
    def _parse_area(text: str) -> Optional[int]:
        # Extrahuj první sekvenci ASCII číslic (zabraňuje '180²' → '1802')
        match = re.search(r"(\d[\d\s]*)", text)
        if match:
            digits = re.sub(r"[^0-9]", "", match.group(1))
            return int(digits) if digits else None
        return None

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kc",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
