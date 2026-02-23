"""
NemovitostiZnojmo.cz scraper (Eurobydleni/Urbium platform).
Strategie: httpx + BeautifulSoup, SSR stránky
Paginace: /reality/page-N
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
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://www.nemovitostiznojmo.cz"
LIST_URL = f"{BASE_URL}/reality/"

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": BASE_URL,
}

PROPERTY_TYPE_MAP = {
    "byt": "Byt", "byty": "Byt",
    "dům": "Dům", "dom": "Dům", "rodinný": "Dům",
    "pozemek": "Pozemek", "parcela": "Pozemek",
    "garáž": "Garáž", "garážové": "Garáž",
    "komerční": "Komerční", "ostatní": "Ostatní",
    "sklep": "Ostatní", "vinný": "Ostatní",
}


class NemovitostiZnojmoScraper:
    """Scraper pro nemovitostiznojmo.cz (Eurobydleni/Urbium platforma)."""

    SOURCE_CODE = "NEMZNOJMO"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 50 if full_rescan else 5
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 5) -> int:
        logger.info("Starting NemovitostiZnojmo scraper (max_pages=%s)", max_pages)
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                page = 1
                while page <= max_pages:
                    url = LIST_URL if page == 1 else f"{LIST_URL}page-{page}"
                    try:
                        with timer(f"Fetch list page {page}"):
                            start = time.perf_counter()
                            html = await self._fetch(url)
                            metrics.record_fetch(time.perf_counter() - start)

                        items, has_next = self._parse_list_page(html)
                        if not items:
                            logger.info("No items on page %s, stopping", page)
                            break

                        logger.info("Page %s: found %s listings", page, len(items))

                        for item in items:
                            try:
                                detail_html = await self._fetch(item["url"])
                                normalized = self._parse_detail_page(detail_html, item)
                                await self._save_listing(normalized)
                                self.scraped_count += 1
                                metrics.increment_scraped()
                                await asyncio.sleep(0.5)
                            except Exception as exc:
                                logger.error("Error processing %s: %s", item.get("url"), exc)
                                metrics.increment_failed()

                        if not has_next:
                            break
                        page += 1
                        await asyncio.sleep(1.0)

                    except httpx.HTTPStatusError as exc:
                        logger.error("HTTP error page %s: %s", page, exc)
                        break
                    except Exception as exc:
                        logger.error("Error page %s: %s", page, exc)
                        metrics.increment_failed()
                        break

        self._http_client = None
        logger.info("NemovitostiZnojmo scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], bool]:
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        # Eurobydleni: listing items jsou <a href="/slug/detail/ID">
        for link in soup.select("a[href*='/detail/']"):
            href = link.get("href", "")
            if not re.search(r"/detail/\d+$", href):
                continue
            full_url = urljoin(BASE_URL, href)
            # Deduplicate
            if any(r["url"] == full_url for r in results):
                continue

            title_el = link.select_one("h2, h3, h4, .title, strong")
            title = title_el.get_text(strip=True) if title_el else ""

            price_text = ""
            for el in link.find_all(string=re.compile(r"Kč|Kc")):
                stripped = el.strip()
                if re.search(r"\d", stripped) and len(stripped) < 50:
                    price_text = stripped
                    break

            results.append({
                "url": full_url,
                "title": title[:200],
                "price_text": price_text,
            })

        # Paginace: číslované odkazy na stránky
        has_next = bool(soup.select("a[href*='page-']"))
        # Kontrola aktivní stránky vs. dostupné
        current_page_links = soup.select("nav a, .pagination a, [class*='page'] a")
        if has_next:
            # Ověřit, že existuje stránka vyšší než aktuální
            page_nums = []
            for a in current_page_links:
                m = re.search(r"page-(\d+)", a.get("href", ""))
                if m:
                    page_nums.append(int(m.group(1)))
            if not page_nums:
                has_next = False

        return results, has_next

    def _parse_price(self, price_text: str) -> Optional[float]:
        if not price_text:
            return None
        match = re.search(r'(\d[\d\s]+)', price_text)
        if match:
            price_str = match.group(1).replace(' ', '').replace('\xa0', '')
            try:
                return float(price_str)
            except ValueError:
                return None
        return None

    def _parse_detail_page(
        self, html: str, list_item: Dict[str, Any]
    ) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "url": list_item["url"],
        }

        # External ID z URL
        match = re.search(r"/detail/(\d+)$", list_item["url"])
        if match:
            result["external_id"] = match.group(1)
        else:
            # Fallback
            result["external_id"] = list_item["url"].split("/")[-1]

        title_el = soup.find("h1") or soup.find("h2")
        result["title"] = (
            title_el.get_text(" ", strip=True)[:200] if title_el else list_item.get("title", "")
        )

        # Cena
        price_text = ""
        for el in soup.find_all(string=lambda t: t and "Kč" in t):
            stripped = el.strip()
            if re.search(r"\d", stripped) and len(stripped) < 50:
                price_text = stripped
                break
        result["price"] = self._parse_price(price_text)

        # Typ nemovitosti z názvu
        title_lower = result["title"].lower()
        result["property_type"] = "Ostatní"
        for keyword, ptype in PROPERTY_TYPE_MAP.items():
            if keyword in title_lower:
                result["property_type"] = ptype
                break

        # Offer type
        result["offer_type"] = "Pronájem" if "pronájem" in title_lower or "pronajm" in title_lower else "Prodej"

        # Popis
        desc_el = soup.select_one(".description, article .content, main p, .perex")
        result["description"] = desc_el.get_text(" ", strip=True)[:5000] if desc_el else ""

        # Parametry (Eurobydleni)
        # Hledáme tabulku nebo seznam parametrů
        for row in soup.select("tr, li, .param-item"):
            text = row.get_text(" ", strip=True).lower()
            
            # Plocha
            if "plocha" in text and ("m2" in text or "m²" in text):
                match = re.search(r'(\d+)\s*m[²2]', text)
                if match:
                    area_val = float(match.group(1))
                    if "pozem" in text or "parcel" in text:
                        result["area_land"] = area_val
                    else:
                        result["area_built_up"] = area_val
            
            # Lokace
            if "lokalita" in text or "adresa" in text or "obec" in text:
                # Zkusíme najít hodnotu vedle labelu
                val_el = row.select_one("td:nth-child(2), span.value, strong")
                if val_el:
                    result["location_text"] = val_el.get_text(strip=True)[:200]
                else:
                    # Fallback na celý text bez labelu
                    clean_text = re.sub(r'^(lokalita|adresa|obec|místo):?\s*', '', text, flags=re.I)
                    if clean_text and len(clean_text) > 3:
                        result["location_text"] = clean_text[:200]

        # Pokud jsme nenašli lokaci v parametrech, zkusíme najít v textu
        if "location_text" not in result or not result["location_text"]:
            loc_candidates = soup.find_all(string=re.compile(r'Znojmo|okres Znojmo', re.I))
            if loc_candidates:
                result["location_text"] = loc_candidates[0].strip()[:200]
            else:
                result["location_text"] = "Znojmo a okolí"

        # Fotky
        photo_urls = []
        for img in soup.select(".gallery img, .fotorama img, a[data-fancybox] img, img.photo"):
            src = img.get("src") or img.get("data-src")
            if src:
                full_url = urljoin(BASE_URL, src)
                if full_url not in photo_urls:
                    photo_urls.append(full_url)
        
        # Fallback pro fotky, pokud selektory selžou
        if not photo_urls:
            for img in soup.find_all("img"):
                src = img.get("src", "")
                if "foto" in src.lower() or "gallery" in src.lower() or "image" in src.lower():
                    full_url = urljoin(BASE_URL, src)
                    if full_url not in photo_urls:
                        photo_urls.append(full_url)

        result["photos"] = photo_urls[:20]

        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        try:
            db = get_db_manager()
            listing_id = await db.upsert_listing(listing)
            logger.info(f"Saved listing {listing_id}: {listing.get('title', 'N/A')[:50]} | {listing.get('price', 'N/A')} Kč")
        except Exception as exc:
            logger.error(f"Failed to save listing: {exc}")
            raise
