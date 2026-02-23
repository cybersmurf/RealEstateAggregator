"""
HV Reality scraper (hvreality.cz).
Strategie: httpx + BeautifulSoup, WordPress/Elementor SSR stránky
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

BASE_URL = "https://hvreality.cz"
START_URLS = [
    "https://hvreality.cz/prodej-nemovitosti/",
    "https://hvreality.cz/pronajem-nemovitosti/"
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

PROPERTY_TYPE_MAP = {
    "byt": "Byt", "byty": "Byt",
    "dům": "Dům", "dom": "Dům", "rodinný": "Dům", "vila": "Dům",
    "pozemek": "Pozemek", "parcela": "Pozemek",
    "garáž": "Garáž", "garážové": "Garáž",
    "komerční": "Komerční", "ostatní": "Ostatní",
    "sklep": "Ostatní", "vinný": "Ostatní", "chalupa": "Dům", "chata": "Dům"
}

class HvRealityScraper:
    """Scraper pro hvreality.cz."""

    SOURCE_CODE = "HVREALITY"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 20 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting HV Reality scraper (max_pages=%s)", max_pages)
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                
                for start_url in START_URLS:
                    page = 1
                    current_url = start_url
                    
                    while page <= max_pages and current_url:
                        try:
                            with timer(f"Fetch list page {page} ({current_url})"):
                                start = time.perf_counter()
                                html = await self._fetch(current_url)
                                metrics.record_fetch(time.perf_counter() - start)

                            items, next_url = self._parse_list_page(html, current_url)
                            if not items:
                                logger.info("No items on page %s, stopping category", page)
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

                            current_url = next_url
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
        logger.info("HV Reality scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str, current_url: str) -> Tuple[List[Dict[str, Any]], Optional[str]]:
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen_urls: set = set()

        def _add_item(href: str, title: str) -> None:
            full_url = urljoin(BASE_URL, href)
            if full_url in seen_urls or full_url.rstrip("/") == current_url.rstrip("/"):
                return
            # Přeskočit kategoriální / paginační linky
            if any(x in full_url for x in ["page/", "/category/", "/author/", "/tag/"]):
                return
            seen_urls.add(full_url)
            results.append({"url": full_url, "title": title[:200] if title else ""})

        # Priorita 1: WordPress .hentry articles (hvreality.cz téma)
        for article in soup.select("article.hentry, .hentry"):
            title_el = article.select_one(".entry-title a, h1 a, h2 a, h3 a, h4 a, h5 a, h6 a")
            if title_el and title_el.get("href"):
                _add_item(title_el["href"], title_el.get_text(strip=True))
                continue
            # Fallback – první non-trivial <a> v article
            for a in article.select("a[href]"):
                href = a.get("href", "")
                if len(href) > 30 and href.startswith("http"):
                    title_txt = article.select_one("h1,h2,h3,h4,h5,h6")
                    _add_item(href, title_txt.get_text(strip=True) if title_txt else a.get_text(strip=True))
                    break

        # Priorita 2: Elementor post grid (fallback pro jiná témata)
        if not results:
            for link in soup.select(
                "a[href*='/property/'], a[href*='/nemovitost/'], "
                ".elementor-post__title a, .elementor-post a"
            ):
                href = link.get("href", "")
                if not href or "#" in href:
                    continue
                title_el = link.select_one("h1, h2, h3, h4, h5, h6")
                title = title_el.get_text(strip=True) if title_el else link.get_text(strip=True)
                _add_item(href, title)

        # Find next page URL
        next_url = None
        next_link = soup.select_one("a.next.page-numbers, a.elementor-pagination__next, .pagination a.next")
        if next_link and next_link.get("href"):
            next_url = urljoin(BASE_URL, next_link.get("href"))
        else:
            for a in soup.find_all("a"):
                text = a.get_text(strip=True).lower()
                if "další" in text or "next" in text or "»" in text:
                    href = a.get("href")
                    if href and "page" in href:
                        next_url = urljoin(BASE_URL, href)
                        break

        return results, next_url

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

    def _parse_detail_page(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "url": list_item["url"],
        }

        # External ID z URL
        result["external_id"] = list_item["url"].strip("/").split("/")[-1]

        title_el = soup.find("h1")
        result["title"] = (
            title_el.get_text(" ", strip=True)[:200] if title_el else list_item.get("title", "")
        )

        # Cena
        price_text = ""
        for el in soup.find_all(string=lambda t: t and ("Kč" in t or "CZK" in t)):
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
        desc_parts = []
        for p in soup.select(".elementor-widget-text-editor p, .entry-content p, article p"):
            text = p.get_text(" ", strip=True)
            if len(text) > 20:
                desc_parts.append(text)
        result["description"] = "\n\n".join(desc_parts)[:5000]

        # Parametry
        for row in soup.select("tr, li, .elementor-icon-list-item"):
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
            if "lokalita" in text or "adresa" in text or "obec" in text or "město" in text:
                clean_text = re.sub(r'^(lokalita|adresa|obec|město):?\s*', '', text, flags=re.I)
                if clean_text and len(clean_text) > 3:
                    result["location_text"] = clean_text.title()[:200]

        if "location_text" not in result or not result["location_text"]:
            loc_candidates = soup.find_all(string=re.compile(r'Znojmo|okres Znojmo', re.I))
            if loc_candidates:
                result["location_text"] = loc_candidates[0].strip()[:200]
            else:
                result["location_text"] = "Znojmo a okolí"

        # Fotky
        photo_urls = []
        for img in soup.select(".gallery img, .elementor-gallery-item img, .swiper-slide img, img[data-src]"):
            src = img.get("data-src") or img.get("data-large_image") or img.get("src")
            if src and not src.endswith(".svg"):
                full_url = urljoin(BASE_URL, src)
                # Odstranění rozlišení z WordPress URL (např. -150x150.jpg -> .jpg)
                full_url = re.sub(r'-\d+x\d+(\.[a-zA-Z]+)$', r'\1', full_url)
                if full_url not in photo_urls:
                    photo_urls.append(full_url)
        
        if not photo_urls:
            for img in soup.find_all("img"):
                src = img.get("src", "")
                if ("foto" in src.lower() or "gallery" in src.lower() or "uploads" in src.lower()) and not src.endswith(".svg"):
                    full_url = urljoin(BASE_URL, src)
                    full_url = re.sub(r'-\d+x\d+(\.[a-zA-Z]+)$', r'\1', full_url)
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
