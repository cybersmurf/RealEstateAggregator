"""
Reality-znojmo.cz scraper – síť REALmix / České reality (ČESKÝ INTERNET s.r.o.).

Stejný obsah je i na sousede.cz a jiho.moravskereality.cz; scrapujeme jednu
doménu (reality-znojmo.cz = výpis pro okres Znojmo). Vkládají sem menší
realitky bez Sreality (Tvůj Makléř, Reamis, BO Reality, Broker Consulting,
Ospera, Home 4 People…), 30. 9. 2026 to bylo 172 domů, 2 ze 14 vzorků
jsme neměli.

Strategie: httpx + BeautifulSoup, SSR HTML.
Výpis: /prodej/rodinne-domy/ (20 položek), další stránky ?strana=N.
Detail: h1, tabulka „Základní údaje“ (Plocha pozemku, Plocha užitná, Cena,
Stav objektu, ID nemovitosti), <p data-block-name="description-text">,
fotky img.ceskereality.cz (foto_detail/…jpg, data-lazy foto/…jpg).
Obec = h3 odkaz / .adress ve výpisu; okres bereme z výpisu (Znojmo).
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

BASE_URL = "https://www.reality-znojmo.cz"

# (cesta výpisu, typ nemovitosti, typ nabídky, okres, který výpis pokrývá)
DEFAULT_LISTS: List[Tuple[str, str, str, str]] = [
    ("/prodej/rodinne-domy/", "Dům", "Prodej", "Znojmo"),
    ("/prodej/byty/", "Byt", "Prodej", "Znojmo"),
    ("/prodej/pozemky/", "Pozemek", "Prodej", "Znojmo"),
]

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": BASE_URL,
}

# Podtyp z URL: /prodej/rodinne-domy/<podtyp>/<obec>/… – chaty jsou v sekci domů
SUBTYPE_MAP = {"chaty": "Chata", "chalupy": "Chata", "rodinne-domy": "Dům", "vily": "Dům",
               "byty": "Byt", "pozemky": "Pozemek", "komercni": "Komerční", "garaze": "Garáž"}


class RealmixScraper:
    """Scraper pro reality-znojmo.cz (síť REALmix)."""

    SOURCE_CODE = "REALMIX"

    def __init__(self, lists: Optional[List[Tuple[str, str, str, str]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 20 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting REALmix scraper (max_pages=%s)", max_pages)
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                for path, property_type, offer_type, district in self.lists:
                    await self._scrape_list(path, property_type, offer_type, district, max_pages, metrics)
        self._http_client = None
        logger.info("REALmix scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    async def _scrape_list(self, path, property_type, offer_type, district, max_pages, metrics) -> None:
        page = 1
        while page <= max_pages:
            url = urljoin(BASE_URL, path if page == 1 else f"{path}?strana={page}")
            try:
                with timer(f"Fetch list {path} page {page}"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
                items, has_next = self.parse_list_page(html, page)
                if not items:
                    logger.info("No items on %s page %s, stopping", path, page)
                    break
                logger.info("%s page %s: %s listings", path, page, len(items))
                for item in items:
                    try:
                        detail_html = await self._fetch(item["url"])
                        normalized = self.parse_detail_page(detail_html, item, property_type, offer_type, district)
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
                logger.error("HTTP error %s page %s: %s", path, page, exc)
                break
            except Exception as exc:
                logger.error("Error %s page %s: %s", path, page, exc)
                metrics.increment_failed()
                break

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── parsování ────────────────────────────────────────────────────────────

    def parse_list_page(self, html: str, page: int = 1) -> Tuple[List[Dict[str, Any]], bool]:
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        for box in soup.select("div.estate"):
            link = box.select_one(".estateContent h3 a[href]") or box.select_one("h3 a[href]") or box.select_one("a[href$='.html']")
            if not link:
                continue
            href = link.get("href", "")
            m = re.search(r"-(\d{5,})\.html$", href)
            if not m:
                continue
            full_url = urljoin(BASE_URL, href)
            if any(r["url"] == full_url for r in results):
                continue
            addr = box.select_one(".adress")
            price_el = box.select_one(".big.text-blue, .text-blue.big")
            results.append({
                "url": full_url,
                "external_id": m.group(1),
                "title": link.get_text(" ", strip=True)[:200],
                "municipality": addr.get_text(" ", strip=True)[:100] if addr else "",
                "price_text": price_el.get_text(" ", strip=True) if price_el else "",
                "thumb": (box.select_one(".estateImage img") or {}).get("src", ""),
            })
        has_next = any(f"strana={page + 1}" in (a.get("href") or "") for a in soup.select("a[href*='strana=']"))
        return results, has_next

    @staticmethod
    def _parse_number(text: str) -> Optional[float]:
        if not text:
            return None
        m = re.search(r"(\d[\d\s\xa0]*)", text)
        if not m:
            return None
        try:
            return float(m.group(1).replace(" ", "").replace("\xa0", ""))
        except ValueError:
            return None

    def parse_detail_page(self, html: str, item: Dict[str, Any], property_type: str, offer_type: str, district: str) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        h1 = soup.find("h1")
        title = h1.get_text(" ", strip=True) if h1 else item.get("title", "")
        title = re.sub(r"\s+", " ", title)[:200]

        # podtyp z URL (/prodej/rodinne-domy/chaty/…)
        ptype = property_type
        m = re.search(r"^/prodej/[^/]+/([^/]+)/", item["url"].replace(BASE_URL, ""))
        if m and m.group(1) in SUBTYPE_MAP:
            ptype = SUBTYPE_MAP[m.group(1)]
        if "pronájem" in title.lower() or "pronajm" in title.lower():
            offer_type = "Pronájem"

        params: Dict[str, str] = {}
        for tr in soup.select("table tr"):
            tds = tr.find_all("td")
            if len(tds) >= 2:
                params[tds[0].get_text(" ", strip=True).lower()] = tds[1].get_text(" ", strip=True)

        price = self._parse_number(params.get("cena", "")) or self._parse_number(item.get("price_text", ""))
        area_land = self._parse_number(params.get("plocha pozemku", ""))
        area_built = self._parse_number(params.get("plocha užitná", "") or params.get("užitná plocha", "")
                                        or params.get("zastavěná plocha", "") or params.get("plocha", ""))
        if ptype == "Pozemek" and not area_land:
            area_land = area_built
            area_built = None

        desc_el = soup.select_one("p[data-block-name='description-text']") or soup.select_one(".estate-detail-description, .description")
        description = desc_el.get_text("\n", strip=True) if desc_el else ""
        rk = None
        rk_el = soup.select_one(".estate-detail-contact .name, .contact-box h3, .rk-name")
        if rk_el:
            rk = rk_el.get_text(" ", strip=True)[:100]
        if rk:
            description = (description + f"\n\nRealitní kancelář: {rk}")[:5000]

        # Výpis dává „Ulice, Obec" – do municipality patří jen obec (poslední část)
        obec = (item.get("municipality") or "").split(",")[-1].strip()
        if not obec:
            crumbs = [a.get_text(strip=True) for a in soup.select(".breadcrumb a")]
            m2 = next((re.sub(r"^Domy |^Byty |^Pozemky ", "", c) for c in crumbs if re.match(r"^(Domy|Byty|Pozemky) [A-ZŠČŘŽÁÉÍÓÚŮŤĎŇĚ]", c)), "")
            obec = m2
        location_text = f"{obec}, okres {district}" if obec else f"okres {district}"

        # Plná velikost je jen img.ceskereality.cz/foto/…jpg (200). Varianta
        # foto_detail/… vrací 302 na redirect stránku a img-cache umí jen 320x320.
        photos: List[str] = []
        for src in re.findall(r"https?://img\.ceskereality\.cz/foto/[^\"'\s]+\.jpe?g", html):
            if src not in photos:
                photos.append(src)
        if not photos and item.get("thumb"):
            photos.append(item["thumb"])

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": item["external_id"],
            "url": item["url"],
            "title": title,
            "description": description[:5000],
            "property_type": ptype,
            "offer_type": offer_type,
            "price": price,
            "location_text": location_text[:200],
            "municipality": obec[:100] or None,
            "district": district,
            "area_built_up": area_built,
            "area_land": area_land,
            "condition": (params.get("stav objektu") or None),
            "photos": photos[:50],
        }

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
