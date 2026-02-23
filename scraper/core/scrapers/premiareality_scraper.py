"""
PREMIA Reality scraper (premiareality.cz).

Strategie: httpx + BeautifulSoup, plný SSR
Kategorie: byty, domy, parcely, rekreace, ostatni
URL pattern: https://www.premiareality.cz/{kategorie}/{slug}-{id}.html
"""
import asyncio
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

BASE_URL = "https://www.premiareality.cz"

CATEGORIES = [
    ("byty",    "Byt"),
    ("domy",    "Dům"),
    ("parcely", "Pozemek"),
    ("rekreace","Dům"),   # chaty, zahrady
    ("ostatni", "Ostatní"),
]

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
}


class PremiaRealityScraper:
    """Scraper pro premiareality.cz (PREMIA Reality s.r.o.)."""

    SOURCE_CODE = "PREMIAREALITY"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        return await self.scrape()

    async def scrape(self) -> int:
        logger.info("Starting PREMIA Reality scraper")
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                for category, default_type in CATEGORIES:
                    list_url = f"{BASE_URL}/{category}/seznam.html"
                    try:
                        with timer(f"Fetch list {category}"):
                            start = time.perf_counter()
                            html = await self._fetch(list_url)
                            metrics.record_fetch(time.perf_counter() - start)

                        items = self._parse_list_page(html)
                        logger.info("Category %s: %s listings", category, len(items))

                        for item in items:
                            item["default_property_type"] = default_type
                            try:
                                detail_html = await self._fetch(item["url"])
                                normalized = self._parse_detail_page(detail_html, item)
                                await self._save_listing(normalized)
                                self.scraped_count += 1
                                metrics.increment_scraped()
                                await asyncio.sleep(0.4)
                            except Exception as exc:
                                logger.error("Error processing %s: %s", item.get("url"), exc)
                                metrics.increment_failed()

                        await asyncio.sleep(1.0)

                    except Exception as exc:
                        logger.error("Error fetching category %s: %s", category, exc)
                        metrics.increment_failed()

        self._http_client = None
        logger.info("PREMIA Reality scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str) -> List[Dict[str, Any]]:
        """Parsuje seznam inzerátů z kategorie. Žádná paginace – vše na jedné stránce."""
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen: set = set()

        for a in soup.select("a[href]"):
            href = a.get("href", "")
            # Detail URL: končí na -{numerické-id}.html
            if not re.search(r"-\d+\.html$", href):
                continue
            # Přeskočit anchor linky vlastní stránky a jiné kategorie
            full_url = urljoin(BASE_URL, href) if href.startswith("/") else href
            if not full_url.startswith(BASE_URL):
                continue
            if full_url in seen:
                continue
            seen.add(full_url)

            # Zkus najít title v sousedních elementech
            title = a.get_text(" ", strip=True)
            if not title or len(title) < 5:
                parent = a.find_parent(["div", "li", "article"])
                if parent:
                    h = parent.find(["h1","h2","h3","h4"])
                    if h:
                        title = h.get_text(" ", strip=True)

            results.append({"url": full_url, "title": title[:200]})

        return results

    def _extract_table_params(self, soup: BeautifulSoup) -> Dict[str, str]:
        """Extrahuje parametry z detailní tabulky (label | value)."""
        params: Dict[str, str] = {}
        for row in soup.select("table tr"):
            cells = row.select("td, th")
            if len(cells) >= 2:
                label = cells[0].get_text(" ", strip=True).lower().rstrip(":")
                value = cells[1].get_text(" ", strip=True)
                if label and value:
                    params[label] = value
        return params

    def _parse_price(self, text: str) -> Optional[float]:
        if not text:
            return None
        clean = re.sub(r"[^\d]", "", text)
        return float(clean) if clean else None

    def _parse_area(self, text: str) -> Optional[float]:
        if not text:
            return None
        match = re.search(r"(\d[\d\s]*)", text)
        if match:
            clean = match.group(1).replace(" ", "").replace("\xa0", "")
            try:
                return float(clean)
            except ValueError:
                return None
        return None

    def _parse_detail_page(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "url": list_item["url"],
        }

        # External ID z URL (poslední číslo před .html)
        id_match = re.search(r"-(\d+)\.html$", list_item["url"])
        result["external_id"] = id_match.group(1) if id_match else list_item["url"].split("/")[-1]

        # Title
        h1 = soup.find("h1")
        result["title"] = h1.get_text(" ", strip=True)[:200] if h1 else list_item.get("title", "")

        # Offer type z URL
        url_lower = list_item["url"].lower()
        result["offer_type"] = "Pronájem" if "pronajem" in url_lower or "pronájem" in url_lower else "Prodej"

        # Parametry z tabulky
        params = self._extract_table_params(soup)

        # Cena
        price_text = params.get("cena", "")
        result["price"] = self._parse_price(price_text)

        # Typ nemovitosti
        nem_type = params.get("nemovitost", "").lower()
        if "byt" in nem_type:
            result["property_type"] = "Byt"
        elif "dům" in nem_type or "dum" in nem_type or "vila" in nem_type or "chata" in nem_type or "chalupa" in nem_type:
            result["property_type"] = "Dům"
        elif "pozemek" in nem_type or "parcela" in nem_type or "zahrada" in nem_type:
            result["property_type"] = "Pozemek"
        elif "garáž" in nem_type or "garaz" in nem_type:
            result["property_type"] = "Garáž"
        elif "komerční" in nem_type or "sklep" in nem_type or "vinný" in nem_type:
            result["property_type"] = "Ostatní"
        else:
            result["property_type"] = list_item.get("default_property_type", "Ostatní")

        # Plochy
        uzitna = params.get("užitná plocha", params.get("uzitna plocha", ""))
        if uzitna:
            result["area_built_up"] = self._parse_area(uzitna)

        zahrada = params.get("plocha zahrady", params.get("plocha pozemku", params.get("plocha parcely", "")))
        if zahrada:
            result["area_land"] = self._parse_area(zahrada)

        # Lokace: Ulice + Město
        ulice = params.get("ulice", "")
        mesto = params.get("město", params.get("mesto", ""))
        if ulice and mesto:
            result["location_text"] = f"{ulice}, {mesto}"
        elif mesto:
            result["location_text"] = mesto
        elif ulice:
            result["location_text"] = ulice
        else:
            # Fallback z title (bývá tam "Na Hrázi - Znojmo")
            h2 = soup.find("h2")
            if h2:
                result["location_text"] = h2.get_text(" ", strip=True)[:200]
            else:
                result["location_text"] = "Znojmo a okolí"

        # Popis – div.col-md-6.ps-5 (dle průzkumu struktury webu)
        desc_el = soup.select_one(".ps-5.pe-5, .ps-5, .col-md-6.ps-5")
        if desc_el:
            # Odstraň vnořené form/tlačítka
            for noise in desc_el.select("a, button, form, .tlacitka"):
                noise.decompose()
            result["description"] = desc_el.get_text(" ", strip=True)[:5000]
        else:
            result["description"] = ""

        # Fotky – parent <a href> u každého <img> v galerii
        photo_urls: List[str] = []
        seen_photos: set = set()
        for img in soup.select(".carousel-detail img, .preview img, img[src*='importestate'], img[src*='estate']"):
            parent_a = img.find_parent("a", href=True)
            if parent_a:
                photo_href = parent_a.get("href", "")
                photo_url = urljoin(BASE_URL, photo_href) if photo_href else ""
            else:
                src = img.get("src") or img.get("data-src", "")
                photo_url = urljoin(BASE_URL, src) if src else ""

            if photo_url and photo_url not in seen_photos:
                # Preferuj verzii bez thumbs suffix – nahraď thumbs cestou na originál
                photo_url = re.sub(r"/thumbs_\d+_\d+/", "/", photo_url)
                seen_photos.add(photo_url)
                photo_urls.append(photo_url)

        # Fallback: všechny imgs přes 5000 pixelů (podle pattern z průzkumu)
        if not photo_urls:
            for img in soup.find_all("img"):
                src = str(img.get("src", "") or img.get("data-src", ""))
                if "importestate" in src or "estate" in src:
                    photo_url = urljoin(BASE_URL, src)
                    if photo_url not in seen_photos:
                        seen_photos.add(photo_url)
                        photo_urls.append(photo_url)

        result["photos"] = photo_urls[:20]

        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        try:
            db = get_db_manager()
            listing_id = await db.upsert_listing(listing)
            logger.info(
                "Saved %s: %s | %s Kč",
                listing_id,
                listing.get("title", "N/A")[:50],
                listing.get("price", "N/A"),
            )
        except Exception as exc:
            logger.error("Failed to save listing: %s", exc)
            raise
