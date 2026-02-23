"""
REMAX scraper for Czech real estate listings.

Optimalizovaný scraper s hybridním přístupem:
- httpx + BeautifulSoup pro list pages (rychlé)
- Playwright jen pro JS-heavy detail pages (pokud je potřeba)
"""
import asyncio
import logging
import re
import time
from typing import Any, List, Dict, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..browser import get_browser_manager
from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)


class RemaxScraper:
    """
    Scraper pro REMAX Czech Republic - Znojmo region.
    
    Strategy:
    1. List pages: httpx + BeautifulSoup (fast)
    2. Detail pages: httpx first, Playwright fallback pokud je JS required
    
    URL structure:
    - List: https://www.remax-czech.cz/reality/{category}/prodej/jihomoravsky-kraj/znojmo/?stranka=1
    - Detail: https://www.remax-czech.cz/reality/detail/{id}/{slug}
    """
    
    BASE_URL = "https://www.remax-czech.cz"
    SOURCE_CODE = "REMAX"
    
    # Znojmo-specific search URLs (domy + pozemky prodej a pronájem)
    SEARCH_CONFIGS = [
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Dům",
        },
        # pronajeti URL vrací 404 – REMAX nemá Znojmo pronájmy domů
        {
            "url": "https://www.remax-czech.cz/reality/pozemky/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Pozemek",
        },
        {
            "url": "https://www.remax-czech.cz/reality/byty/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Byt",
        },
    ]
    
    def __init__(self, use_playwright_for_details: bool = False):
        """
        Args:
            use_playwright_for_details: Pokud True, použije Playwright i na detail pages
        """
        self.use_playwright_for_details = use_playwright_for_details
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None
    
    async def run(self, full_rescan: bool = False) -> int:
        """
        Hlavní entry point volaný z runner.py.
        
        Args:
            full_rescan: Pokud True, scrapuje všechny stránky, jinak jen prvních 5
            
        Returns:
            Počet úspěšně scrapnutých inzerátů
        """
        max_pages = 100 if full_rescan else 5
        total = 0
        for config in self.SEARCH_CONFIGS:
            count = await self.scrape(config["url"], config["offer_type"], config["property_type"], max_pages=max_pages)
            total += count
        return total
    
    async def scrape(self, search_url: str, offer_type: str, property_type: str, max_pages: int = 5) -> int:
        """
        Hlavní entry point pro scraping.
        
        Args:
            max_pages: Maximální počet list pages k procházení (default 5 pro testing)
            
        Returns:
            Počet úspěšně scrapnutých inzerátů
        """
        logger.info(f"Starting REMAX scraper for {search_url} (max_pages={max_pages})")
        
        with scraper_metrics_context() as metrics:
            # Reuse HTTP client pro všechny requesty
            async with httpx.AsyncClient(timeout=30, follow_redirects=True) as client:
                self._http_client = client
                
                page = 1
                
                while page <= max_pages:
                    url = f"{search_url}?stranka={page}"
                    
                    try:
                        with timer(f"Fetch list page {page}"):
                            start = time.perf_counter()
                            html = await self._fetch_page_http(url)
                            metrics.record_fetch(time.perf_counter() - start)
                            
                        with timer(f"Parse list page {page}"):
                            start = time.perf_counter()
                            items = self._parse_list_page(html)
                            metrics.record_parse(time.perf_counter() - start)
                        
                        if not items:
                            logger.info(f"No more items on page {page}, stopping")
                            break

                        # Zpracuj items
                        for item in items:
                            # Předáme hint pro offer/property typ z URL konfigurace
                            item["offer_type_hint"] = offer_type
                            item["property_type_hint"] = property_type
                            try:
                                # Fetch detail page pro kompletní data
                                detail_url = item["detail_url"]
                                detail_html = await self._fetch_page_http(detail_url)
                                normalized = self._parse_detail_page(detail_html, item)
                                
                                await self._save_listing(normalized)
                                self.scraped_count += 1
                                metrics.increment_scraped()
                                
                            except Exception as exc:
                                logger.error(f"Error processing item {item.get('title', 'N/A')}: {exc}")
                                metrics.increment_failed()

                        page += 1
                        await asyncio.sleep(1)  # Throttling - respektuj servery
                        
                    except Exception as exc:
                        logger.error(f"Error scraping page {page}: {exc}")
                        metrics.increment_failed()
                        break
                        
                self._http_client = None
        
        logger.info(f"REMAX scraper finished. Scraped {self.scraped_count} listings")
        return self.scraped_count

    @http_retry
    async def _fetch_page_http(self, url: str) -> str:
        """
        Stáhne HTML stránky pomocí httpx (fast).
        Při HTTP 429/503 nebo síťových chybách se automaticky opakuje (max 3×).
        """
        logger.debug(f"Fetching via HTTP: {url}")
        
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
            
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str) -> List[Dict[str, Any]]:
        """
        Parsuje list stránku s inzeráty.
        
        Selektory jsou založené na skutečné struktuře REMAX webu (leden 2026).
        """
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        # Najdi všechny inzeráty (odkazy na detail)
        # REMAX používá <a href="/reality/detail/..."> strukturu
        for link in soup.select('a[href*="/reality/detail/"]'):
            href = link.get('href', '')
            if not href or '/reality/detail/' not in href:
                continue
                
            # Získej absolutní URL
            detail_url = urljoin(self.BASE_URL, href)
            
            # Extrahuj ID z URL (např. /reality/detail/423340/prodej-domu-123-m2-rakovnik)
            match = re.search(r'/reality/detail/(\d+)/', href)
            if not match:
                continue
            external_id = match.group(1)
            
            # Zkus najít title z textu odkazu nebo parent elementu
            title = link.get_text(strip=True)
            if not title:
                # Zkus parent element
                parent = link.find_parent()
                if parent:
                    title = parent.get_text(' ', strip=True)
            
            if not title or len(title) < 5:
                continue
            
            results.append({
                "source_code": self.SOURCE_CODE,
                "external_id": external_id,
                "detail_url": detail_url,
                "title": title[:200],  # Limit title length
            })

        # Deduplikace podle external_id (u REMAX se často opakují odkazy)
        seen = set()
        unique_results = []
        for item in results:
            ext_id = item["external_id"]
            if ext_id not in seen:
                seen.add(ext_id)
                unique_results.append(item)

        logger.debug(f"Parsed {len(unique_results)} unique items from list page")
        return unique_results

    def _parse_detail_page(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        """
        Parsuje detail stránku inzerátu.
        
        Selektory jsou založené na skutečné struktuře REMAX detailu (leden 2026).
        """
        soup = BeautifulSoup(html, "html.parser")

        # Extrahuj hlavní data
        result = {
            "source_code": self.SOURCE_CODE,
            "external_id": list_item["external_id"],
            "url": list_item["detail_url"],
        }
        
        # Title (z H1 nebo page title)
        h1 = soup.find('h1')
        if h1:
            result["title"] = h1.get_text(' ', strip=True)[:200]
        else:
            result["title"] = list_item.get("title", "")
        
        # Location — zkusí různé strategie
        location_text = ""
        # 1) Hledat breadcrumb nebo meta lokaci
        for sel in ['[class*="location"]', '[class*="address"]', '[class*="breadcrumb"]', '.property-location']:
            el = soup.select_one(sel)
            if el:
                location_text = el.get_text(' ', strip=True)[:200]
                break
        # 2) Textové uzly s "ulice", "část obce", "okres"
        if not location_text:
            location_candidates = soup.find_all(string=re.compile(r'ulice|část obce|okres|Znojmo', re.I))
            if location_candidates:
                location_text = location_candidates[0].strip()[:200]
        # 3) Záloha: extrahuj z URL (REMAX URLs: /reality/detail/ID/prodej-domu-znojmo-...)
        if not location_text:
            url_slug = list_item.get("detail_url", "")
            slug_match = re.search(r'/reality/detail/\d+/(.+)', url_slug)
            if slug_match:
                location_text = slug_match.group(1).replace('-', ' ')[:200]
        result["location_text"] = location_text
        
        # Price (hledej čísla s "Kč")
        price_text = soup.find(string=re.compile(r'(\d[\d\s]+)\s*Kč'))
        if price_text:
            price_match = re.search(r'(\d[\d\s]+)\s*Kč', price_text)
            if price_match:
                price_str = price_match.group(1).replace(' ', '').replace('\xa0', '')
                try:
                    result["price"] = float(price_str)
                except ValueError:
                    result["price"] = None
        
        # Description (dlouhý text odstavce)
        description_parts = []
        for p in soup.find_all(['p', 'div'], limit=20):
            text = p.get_text(' ', strip=True)
            if len(text) > 100:  # Delší odstavce jsou pravděpodobně popis
                description_parts.append(text)
            if len(description_parts) >= 3:  # Max 3 odstavce
                break
        result["description"] = '\n\n'.join(description_parts)[:2000]
        
        # Area (hledej "m²" nebo "m2")
        area_texts = soup.find_all(string=re.compile(r'(\d+)\s*m[²2]'))
        if area_texts:
            for area_text in area_texts[:2]:  # Max 2 first matches
                match = re.search(r'(\d+)\s*m[²2]', area_text)
                if match:
                    area_value = float(match.group(1))
                    # Rozlišíme zastavěnou plochu vs pozemek podle kontextu
                    if 'pozemek' in area_text.lower() or 'parcela' in area_text.lower():
                        result["area_land"] = area_value
                    else:
                        if "area_built_up" not in result:
                            result["area_built_up"] = area_value
        
        # Photos (hledej <img> s URL obsahujícími "remax")
        photo_urls = []
        for img in soup.find_all('img'):
            src = img.get('src', '')
            if 'mlsf.remax-czech.cz' in src or '/data/' in src:
                # Absolutní URL
                photo_url = urljoin(self.BASE_URL, src)
                if photo_url not in photo_urls:
                    photo_urls.append(photo_url)
        result["photos"] = photo_urls[:20]  # Max 20 photos
        
        # Property type (dedukuj z titulu)
        title_lower = result.get("title", "").lower()
        if "dům" in title_lower or "domu" in title_lower or "vila" in title_lower or "vilay" in title_lower:
            result["property_type"] = "Dům"
        elif "byt" in title_lower or "bytu" in title_lower:
            result["property_type"] = "Byt"
        elif "pozemek" in title_lower:
            result["property_type"] = "Pozemek"
        elif "komerč" in title_lower or "sklado" in title_lower or "kancelář" in title_lower:
            result["property_type"] = "Komerční"
        else:
            # Záloha: použij hint ze search config
            result["property_type"] = list_item.get("property_type_hint", "Ostatní")
        
        # Offer type (prodej vs pronájem) - záloha: hint ze search config URL
        if "pronájem" in title_lower or "pronajem" in title_lower:
            result["offer_type"] = "Pronájem"
        elif "prodej" in title_lower:
            result["offer_type"] = "Prodej"
        else:
            result["offer_type"] = list_item.get("offer_type_hint", "Prodej")
        
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        """
        Uloží listing do databáze pomocí DatabaseManager.
        """
        try:
            db = get_db_manager()
            listing_id = await db.upsert_listing(listing)
            logger.info(f"Saved listing {listing_id}: {listing.get('title', 'N/A')[:50]} | {listing.get('price', 'N/A')} Kč")
        except Exception as exc:
            logger.error(f"Failed to save listing: {exc}")
            raise

