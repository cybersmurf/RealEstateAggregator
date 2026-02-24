"""
Prodejme.to scraper (prodejme.to/nabidky).

Site characteristics:
- Listing page renders via AJAX endpoint POST /nabidky/ajax/
  with params: page=N&sold=0
  response: { count: 55, html: '<div class="project-item">...' }
- Each page returns ~9 items; paginate until we collect all
- Detail URL: /nabidky/{slug}
- Photos: /media/estate/upload/{id}/{hash}_{file}.jpg
"""
import asyncio
import logging
import math
import re
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..http_utils import http_retry

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.prodejme.to"
LISTING_URL = f"{BASE_URL}/nabidky/"
AJAX_URL = f"{BASE_URL}/nabidky/ajax/"

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Referer": LISTING_URL,
    "X-Requested-With": "XMLHttpRequest",
}

SKIP_STATUSES = {"Prodano", "Prodáno", "Pronajato"}
STATUS_LABELS = {
    "Novinka",
    "Prodej",
    "Pronájem",
    "Pronajem",
    "Rezervováno",
    "Rezervovano",
    "Prodáno",
    "Prodano",
    "Pronajato",
}


class ProdejmeToScraper:
    """Scraper for Prodejme.to listings."""

    SOURCE_CODE = "PRODEJMETO"
    PAGE_SIZE = 9  # items per AJAX page

    def __init__(self, include_sold: bool = False):
        self.include_sold = include_sold
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        return await self.scrape(include_sold=full_rescan)

    async def scrape(self, include_sold: bool = False) -> int:
        logger.info("Starting Prodejme.to scraper (include_sold=%s)", include_sold)

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                with timer("Fetch all listing pages via AJAX"):
                    items = await self._fetch_all_pages(include_sold=include_sold)

                logger.info("Found %s listings to process", len(items))

                for item in items:
                    try:
                        detail_html = await self._fetch_get(item["detail_url"])
                        normalized = self._parse_detail(detail_html, item)
                        await self._save_listing(normalized)
                        self.scraped_count += 1
                        metrics.increment_scraped()
                        await asyncio.sleep(0.5)
                    except Exception as exc:
                        logger.error("Error processing %s: %s", item.get("detail_url"), exc)
                        metrics.increment_failed()

                self._http_client = None

        logger.info("Prodejme.to scraper finished. Scraped %s listings", self.scraped_count)
        return self.scraped_count

    async def _fetch_all_pages(self, include_sold: bool = False) -> List[Dict[str, Any]]:
        """Fetch all listing pages from the AJAX endpoint and return merged list."""
        all_items: List[Dict[str, Any]] = []
        seen_slugs: set = set()

        # Fetch page 1 to get total count
        first_response = await self._fetch_ajax_page(1, include_sold)
        total_count = first_response.get("count", 0)
        total_pages = max(1, math.ceil(total_count / self.PAGE_SIZE))
        logger.info("Prodejme.to: total=%s listings on %s pages", total_count, total_pages)

        for page_num in range(1, total_pages + 1):
            if page_num == 1:
                response = first_response
            else:
                response = await self._fetch_ajax_page(page_num, include_sold)
                await asyncio.sleep(0.3)

            items = self._parse_ajax_html(response.get("html", ""), include_sold, seen_slugs)
            all_items.extend(items)
            logger.debug("Page %s: %s items (cumulative: %s)", page_num, len(items), len(all_items))

        return all_items

    @http_retry
    async def _fetch_ajax_page(self, page: int, include_sold: bool = False) -> Dict[str, Any]:
        """POST to /nabidky/ajax/ and return parsed JSON response."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        sold_param = "1" if include_sold else "0"
        response = await self._http_client.post(
            AJAX_URL,
            data={"page": page, "sold": sold_param},
            headers={"Content-Type": "application/x-www-form-urlencoded"},
        )
        response.raise_for_status()
        return response.json()

    @http_retry
    async def _fetch_get(self, url: str) -> str:
        """GET request for detail pages."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_ajax_html(
        self,
        html: str,
        include_sold: bool,
        seen_slugs: set,
    ) -> List[Dict[str, Any]]:
        """Parse project-item cards from AJAX HTML fragment."""
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        for card in soup.find_all("div", class_="project-item"):
            # Extract href / slug from title link
            title_tag = card.select_one("h3.title a, h2.title a")
            if not title_tag:
                continue
            href = title_tag.get("href", "")
            slug = href.rstrip("/").split("/")[-1]
            if not slug or slug in seen_slugs:
                continue

            # Badges: Novinka, Prodej, Pronájem, Prodáno, …
            badges = [
                b.get_text(strip=True)
                for b in card.select("div.badge, span.badge")
            ]

            is_sold = any(b in SKIP_STATUSES for b in badges)
            if is_sold and not include_sold:
                continue

            offer_type = (
                "Pronájem" if any("pronaj" in b.lower() or b == "Pronajem" for b in badges)
                else "Prodej"
            )

            title = title_tag.get_text(" ", strip=True)[:200]

            # Price from <span> inside project-content
            price_span = card.select_one("div.project-content span")
            price_text = price_span.get_text(strip=True) if price_span else ""

            # Thumbnail
            img = card.select_one("div.project-thumb img")
            thumbnail = img.get("src", "") if img else ""

            seen_slugs.add(slug)
            results.append({
                "source_code": self.SOURCE_CODE,
                "external_id": slug,
                "detail_url": urljoin(BASE_URL, href),
                "title": title,
                "price_text": price_text,
                "offer_type": offer_type,
                "is_sold": is_sold,
                "badges": badges,
                "thumbnail": thumbnail,
            })

        return results

    def _parse_detail(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": list_item["external_id"],
            "url": list_item["detail_url"],
            "offer_type": self._normalize_offer_type(list_item.get("offer_type", "Prodej")),
        }

        # Prodejme.to používá h2 jako hlavní nadpis na detail stránce
        h1 = soup.find("h1") or soup.find("h2")
        result["title"] = h1.get_text(" ", strip=True)[:200] if h1 else list_item.get("title", "")

        params = self._parse_params(soup)

        result["offer_type"] = self._normalize_offer_type(
            params.get("Typ nabidky", result["offer_type"])
        )

        result["property_type"] = self._infer_property_type(
            params.get("Typ"),
            params.get("Druh objektu"),
            params.get("Typ objektu"),
            result["title"],
        )

        result["price"] = self._parse_price(params.get("Cena", list_item.get("price_text", "")))
        result["area_built_up"] = self._parse_area(
            params.get("Uzitna plocha") or params.get("Velikost") or ""
        )
        result["area_land"] = self._parse_area(params.get("Velikost pozemku") or "")

        locality = params.get("Lokalita") or params.get("Lokalita obec") or ""
        region = params.get("Lokalita kraj") or ""
        result["location_text"] = locality or region

        result["description"] = self._extract_description(soup)
        result["photos"] = self._extract_photos(soup)

        return result

    def _parse_params(self, soup: BeautifulSoup) -> Dict[str, str]:
        params: Dict[str, str] = {}

        for ul in soup.find_all("ul"):
            for li in ul.find_all("li"):
                text = li.get_text(" ", strip=True)
                if ":" in text:
                    label, value = [part.strip() for part in text.split(":", 1)]
                    if label and value:
                        params[self._normalize_label(label)] = value

                spans = li.find_all("span")
                if len(spans) >= 2:
                    label = spans[0].get_text(" ", strip=True)
                    value = spans[1].get_text(" ", strip=True)
                    if label and value:
                        params[self._normalize_label(label)] = value

        for li in soup.select(".param, .params li"):
            text = li.get_text(" ", strip=True)
            if ":" in text:
                label, value = [part.strip() for part in text.split(":", 1)]
                if label and value:
                    params[self._normalize_label(label)] = value

        return params

    def _normalize_label(self, label: str) -> str:
        cleaned = label.strip().lower().replace(":", "")
        replacements = {
            "cena": "Cena",
            "lokalita": "Lokalita",
            "lokalita obec": "Lokalita obec",
            "lokalita kraj": "Lokalita kraj",
            "typ nabidky": "Typ nabidky",
            "typ nabídky": "Typ nabidky",
            "uzitna plocha": "Uzitna plocha",
            "užitná plocha": "Uzitna plocha",
            "velikost": "Velikost",
            "velikost pozemku": "Velikost pozemku",
            "typ": "Typ",
            "druh objektu": "Druh objektu",
            "typ objektu": "Typ objektu",
        }
        return replacements.get(cleaned, label.strip())

    def _normalize_offer_type(self, value: str) -> str:
        if not value:
            return "Prodej"
        lowered = value.lower()
        if "pronaj" in lowered:
            return "Pronájem"
        return "Prodej"

    def _infer_property_type(self, *candidates: Optional[str]) -> str:
        text = " ".join([c for c in candidates if c])
        lowered = text.lower()
        if "byt" in lowered:
            return "Byt"
        if "pozem" in lowered:
            return "Pozemek"
        if "chata" in lowered or "chalup" in lowered:
            return "Chata"
        if "komerc" in lowered or "komer" in lowered or "kancel" in lowered:
            return "Komerční"
        if "dum" in lowered or "dům" in lowered or "rodin" in lowered:
            return "Dům"
        return "Ostatní"

    def _extract_description(self, soup: BeautifulSoup) -> str:
        paragraphs = []
        for p in soup.find_all("p"):
            text = p.get_text(" ", strip=True)
            if len(text) > 40:
                paragraphs.append(text)
            if len(paragraphs) >= 5:
                break
        return "\n\n".join(paragraphs)[:5000]

    def _extract_photos(self, soup: BeautifulSoup) -> List[str]:
        urls: List[str] = []
        for link in soup.select("a[href*='/upload/']"):
            href = link.get("href")
            if href and href not in urls:
                urls.append(urljoin(BASE_URL, href))
        for img in soup.select("img[src*='/upload/']"):
            src = img.get("src")
            if src and src not in urls:
                urls.append(urljoin(BASE_URL, src))
        return urls[:20]

    @staticmethod
    def _parse_price(text: str) -> Optional[int]:
        digits = "".join(c for c in text if c.isdigit())
        return int(digits) if digits else None

    @staticmethod
    def _parse_area(text: str) -> Optional[int]:
        match = re.search(r"(\d+)", text)
        return int(match.group(1)) if match else None

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kc",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
