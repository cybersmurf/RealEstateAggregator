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
    
    # Znojmo + Brno-venkov search URLs (domy + pozemky + byty)
    SEARCH_CONFIGS = [
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Dům",
        },
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/brno-venkov/",
            "offer_type": "Prodej",
            "property_type": "Dům",
        },
        {
            "url": "https://www.remax-czech.cz/reality/pozemky/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Pozemek",
        },
        {
            "url": "https://www.remax-czech.cz/reality/pozemky/prodej/jihomoravsky-kraj/brno-venkov/",
            "offer_type": "Prodej",
            "property_type": "Pozemek",
        },
        {
            "url": "https://www.remax-czech.cz/reality/byty/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Byt",
        },
        {
            "url": "https://www.remax-czech.cz/reality/byty/prodej/jihomoravsky-kraj/brno-venkov/",
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

                                # Zkontroluj, zda inzerát není prodaný/rezervovaný
                                if self._detect_sold_status(detail_html):
                                    logger.info(f"Listing {item['external_id']} is sold/reserved – deactivating")
                                    db = get_db_manager()
                                    await db.deactivate_listing(self.SOURCE_CODE, item["external_id"])
                                    metrics.increment_scraped()
                                    continue

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

    def _detect_sold_status(self, html: str) -> bool:
        """
        Detekuje, zda inzerát na detailní stránce REMAX nese status "Prodáno",
        "Rezervováno" nebo "Pronajato" (tj. již není dostupný).

        Hledá v:
          - elementech s třídami obsahujícími "status", "label", "badge", "ribbon"
          - titulku stránky (h1, h2)
          - title tagu
        """
        soup = BeautifulSoup(html, "html.parser")
        sold_keywords = {"prodáno", "rezervováno", "pronajato", "prodano", "rezervovano"}

        # Kontrola title tagu
        title_tag = soup.find("title")
        if title_tag:
            if any(kw in title_tag.get_text().lower() for kw in sold_keywords):
                return True

        # Kontrola h1/h2 nadpisů
        for heading in soup.find_all(["h1", "h2"]):
            if any(kw in heading.get_text().lower() for kw in sold_keywords):
                return True

        # Kontrola elementů se status/label/badge/ribbon třídami
        for el in soup.find_all(class_=True):
            classes = " ".join(el.get("class", []))
            if any(c in classes for c in ("status", "label", "badge", "ribbon", "stamp", "tag")):
                if any(kw in el.get_text().lower() for kw in sold_keywords):
                    return True

        # Kontrola data-atributů (REMAX občas používá data-status)
        for el in soup.find_all(attrs={"data-status": True}):
            if any(kw in el["data-status"].lower() for kw in sold_keywords):
                return True

        return False

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

        Selektory jsou založené na skutečné struktuře REMAX detailu (březen 2026):
        - .pd-base-info__content-collapse-inner  → popis nemovitosti
        - .pd-detail-info__row                   → parametry (label + value)
        - .pd-header__address                    → adresa/lokace
        - .pd-header__price                      → cena
        - .pictogram__item[data-toggle=tooltip]  → ikony (pokoje, plochy)
        """
        soup = BeautifulSoup(html, "html.parser")

        result = {
            "source_code": self.SOURCE_CODE,
            "external_id": list_item["external_id"],
            "url": list_item["detail_url"],
        }

        # ── Title ──────────────────────────────────────────────────────────────
        title_el = soup.select_one('h2.pd-header__title') or soup.find('h1')
        if title_el:
            result["title"] = title_el.get_text(' ', strip=True)[:200]
        else:
            result["title"] = list_item.get("title", "")

        # ── Location ───────────────────────────────────────────────────────────
        # 1) Strukturovaná adresa z pd-header__address
        addr_el = soup.select_one('.pd-header__address')
        if addr_el:
            # Odstraň případný "mapa" odkaz z poslední části
            location_text = addr_el.get_text(' ', strip=True)
            location_text = re.sub(r'\s*mapa\s*$', '', location_text, flags=re.I).strip()
        else:
            # 2) Záloha: hint ze scrapnutého list_item
            location_text = list_item.get("location_text", "")
        result["location_text"] = location_text[:200]

        # ── Description ────────────────────────────────────────────────────────
        # Cílový selektor: .pd-base-info__content-collapse-inner
        desc_el = soup.select_one('.pd-base-info__content-collapse-inner')
        if desc_el:
            result["description"] = desc_el.get_text(' ', strip=True)[:5000]
        else:
            # Záloha: h4 perex + první obsáhlý odstavec
            parts = []
            h4 = soup.select_one('.pd-base-info__content h4')
            if h4:
                parts.append(h4.get_text(' ', strip=True))
            result["description"] = ' '.join(parts)[:5000]

        # ── Structured parameters from pd-detail-info__row ─────────────────────
        params: Dict[str, str] = {}
        for row in soup.select('.pd-detail-info__row'):
            label_el = row.select_one('.pd-detail-info__label')
            value_el = row.select_one('.pd-detail-info__value')
            if label_el and value_el:
                label = label_el.get_text(strip=True).rstrip(':').strip()
                value = value_el.get_text(' ', strip=True)
                params[label] = value

        # Užitná plocha / Plocha parcely
        if 'Užitná plocha' in params:
            m = re.search(r'(\d[\d\s]*)', params['Užitná plocha'])
            if m:
                result["area_built_up"] = float(m.group(1).replace(' ', '').replace('\xa0', ''))
        if 'Plocha parcely' in params:
            m = re.search(r'(\d[\d\s]*)', params['Plocha parcely'])
            if m:
                result["area_land"] = float(m.group(1).replace(' ', '').replace('\xa0', ''))

        # Stav objektu → condition
        if 'Stav objektu' in params:
            result["condition"] = params['Stav objektu']

        # Druh objektu → construction_type (Cihlová, Panel, Dřevostavba…)
        if 'Druh objektu' in params:
            result["construction_type"] = params['Druh objektu']

        # ── Disposition (pokoje) ───────────────────────────────────────────────
        # Zkus pictogram__item s title="Počet pokojů"
        for item in soup.select('.pictogram__item'):
            if item.get('title', '') == 'Počet pokojů':
                raw = item.get_text(strip=True)
                m = re.search(r'(\d+\+(?:\d+|kk))', raw, re.I)
                if m:
                    result["disposition"] = m.group(1).upper().replace('KK', 'kk')
                    break
        # Záloha: regex na titulek
        if 'disposition' not in result:
            disp_m = re.search(r'(\d+\+(?:\d+|kk))', result.get("title", ""), re.I)
            if disp_m:
                result["disposition"] = disp_m.group(1).upper().replace('KK', 'kk')

        # ── Price ──────────────────────────────────────────────────────────────
        price_el = soup.select_one('.pd-header__price')
        if price_el:
            price_text = price_el.get_text(' ', strip=True)
            price_m = re.search(r'([\d\s\xa0]+)\s*Kč', price_text)
            if price_m:
                try:
                    result["price"] = float(
                        price_m.group(1).replace(' ', '').replace('\xa0', '').replace('\u202f', '')
                    )
                except ValueError:
                    pass
        if 'price' not in result:
            # Záloha: první výskyt "čísla Kč" na stránce
            price_node = soup.find(string=re.compile(r'(\d[\d\s\xa0]+)\s*Kč'))
            if price_node:
                pm = re.search(r'([\d\s\xa0]+)\s*Kč', price_node)
                if pm:
                    try:
                        result["price"] = float(
                            pm.group(1).replace(' ', '').replace('\xa0', '').replace('\u202f', '')
                        )
                    except ValueError:
                        pass

        # ── Photos ────────────────────────────────────────────────────────────
        photo_urls = []
        for img in soup.find_all('img'):
            src = img.get('src', '')
            if 'mlsf.remax-czech.cz' in src or ('/data/' in src and 'remax' in src):
                photo_url = urljoin(self.BASE_URL, src)
                if photo_url not in photo_urls:
                    photo_urls.append(photo_url)
        result["photos"] = photo_urls[:50]

        # ── Property type ─────────────────────────────────────────────────────
        title_lower = result.get("title", "").lower()
        typ_param = params.get('Typ nemovitosti', '').lower()
        if "dům" in title_lower or "domu" in title_lower or "vila" in title_lower or "domy" in typ_param:
            result["property_type"] = "Dům"
        elif "byt" in title_lower or "bytu" in title_lower or "byty" in typ_param:
            result["property_type"] = "Byt"
        elif "pozemek" in title_lower or "pozemky" in typ_param:
            result["property_type"] = "Pozemek"
        elif any(kw in title_lower for kw in ['komerč', 'sklado', 'kancelář', 'provozov']):
            result["property_type"] = "Komerční"
        else:
            result["property_type"] = list_item.get("property_type_hint", "Ostatní")

        # ── Offer type ────────────────────────────────────────────────────────
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

