"""
REMAX scraper for Czech real estate listings.

Optimalizovaný scraper s hybridním přístupem:
- httpx + BeautifulSoup pro list pages (rychlé)
- Playwright jen pro JS-heavy detail pages (pokud je potřeba)
"""
import asyncio
import logging
import time
from typing import Any, List, Dict, Optional

import httpx
from bs4 import BeautifulSoup

from ..browser import get_browser_manager, close_browser_manager
from ..utils import timer, scraper_metrics_context

logger = logging.getLogger(__name__)


class RemaxScraper:
    """
    Scraper pro REMAX Czech Republic.
    
    Strategy:
    1. List pages: httpx + BeautifulSoup (fast)
    2. Detail pages: httpx first, Playwright fallback pokud je JS required
    """
    
    BASE_URL = "https://www.remax-czech.cz/reality/vyhledavani/"
    SOURCE_CODE = "REMAX"
    , use_playwright={self.use_playwright_for_details})")
        
        with scraper_metrics_context() as metrics:
            # Reuse HTTP client pro všechny requesty
            async with httpx.AsyncClient(timeout=30) as client:
                self._http_client = client
                
                page = 1
                max_pages = 5  # Pro testování omezíme na 5 stránek
                
                while page <= max_pages:
                    url = f"{self.BASE_URL}?page={page}"
                    
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

                        # Zpracuj items - pro testování jen uložíme list data
                        for item in items:
                            try:
                                # Optional: fetch detail page
                                # if self.use_playwright_for_details:
                                #     detail_html = await self._fetch_detail_playwright(item["detail_url"])
                                # else:
                                #     detail_html = await self._fetch_page_http(item["detail_url"])
                                # normalized = self._parse_detail_page(detail_html, item)
                                # await self._save_listing(normalized)
                                
                                await self._save_listing(item)
                                self.scraped_count += 1
                                metrics.increment_scraped()
                                
                            except Exception as exc:
                                logger.error(f"Error processing item: {exc}")
                                metrics.increment_failed()

                        page += 1
                        await asyncio.sleep(1)  # Throttling - respektuj servery
                        
                    except Exception as exc:
                        logger.error(f"Error scraping page {page}: {exc}")
                        metrics.increment_failed()
                        break
                        
                self._http_client = None
                    for i_http(self, url: str) -> str:
        """
        Stáhne HTML stránky pomocí httpx (fast).
        Používej pro non-JS stránky.
        """
        logger.debug(f"Fetching via HTTP: {url}")
        
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
            
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text
        
    async def _fetch_detail_playwright(self, url: str) -> str:
        """
        Stáhne detail stránku pomocí Playwright (slower, pro JS required pages).
        """
        logger.debug(f"Fetching via Playwright: {url}")
        
        browser_manager = await get_browser_manager(
            headless=True,
            max_concurrent=8,
            block_resources=True,
        )
        
        html = await browser_manager.fetch_page(
            url,
            wait_for_selector=".remax-property-detail",  # Přizpůsob podle skutečného HTML
            wait_for_state="domcontentloaded",
        )
        
        return html
                        # Pro zjednodušení uložíme jen list data
                        await self._save_listing(item)
                        self.scraped_count += 1

                    page += 1
                    await asyncio.sleep(1)  # Throttling - respektuj servery
                    
                except Exception as exc:
                    logger.error(f"Error scraping page {page}: {exc}")
                    break
        
        logger.info(f"REMAX scraper finished. Scraped {self.scraped_count} listings")
        return self.scraped_count

    async def _fetch_page(self, client: httpx.AsyncClient, url: str) -> str:
        """Stáhne HTML stránky."""
        logger.debug(f"Fetching {url}")
        response = await client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str) -> List[Dict[str, Any]]:
        """
        Parsuje list stránku s inzeráty.
        
        NOTE: Toto je MOCK implementace - skutečné HTML selektory se liší!
        Musíš si prohlédnout HTML strukturu REMAX webu a upravit selektory.
        """
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        # MOCK - tady si přizpůsobíš selektory podle skutečného HTML Remaxu
        # Pro testování vracíme prázdný list (protože nemáme skutečné HTML)
        # for card in soup.select(".remax-search-result-item"):
        #     title_el = card.select_one(".remax-search-result-title")
        #     url_el = card.select_one("a")
        #     location_el = card.select_one(".remax-search-result-location")
        #
        #     if not title_el or not url_el:
        #         continue
        #
        #     results.append({
        #         "source_code": self.SOURCE_CODE,
        #         "title": title_el.get_text(strip=True),
        #         "detail_url": url_el["href"],
        #         "location_text": location_el.get_text(strip=True) if location_el else "",
        #     })

        logger.debug(f"Parsed {len(results)} items from list page")
        return results

    def _parse_detail_page(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        """
        Parsuje detail stránku inzerátu.
        
        NOTE: MOCK implementace - upravit podle skutečného HTML!
        """
        soup = BeautifulSoup(html, "html.parser")

        # MOCK - přizpůsob selektory podle konkrétní struktury detailu
        # description_el = soup.select_one(".remax-property-description")
        # description = description_el.get_text("\n", strip=True) if description_el else ""

        return {
            "source_code": self.SOURCE_CODE,
            "title": list_item["title"],
            "url": list_item["detail_url"],
            "location_text": list_item["location_text"],
            "description": "",  # description
            # další pole: cena, plocha, typ, fotky...
        }

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        """
        Uloží listing do databáze.
        
        TODO: Implementovat DB persistence
        - asyncpg / SQLAlchemy
        - INSERT/UPDATE into Listings, ListingPhotos
        - Normalizace dat podle DB schema
        """
        # Pro testování jen logujeme
        logger.debug(f"Saving listing: {listing.get('title', 'N/A')}")
        # await db.save_listing(listing)
        pass
