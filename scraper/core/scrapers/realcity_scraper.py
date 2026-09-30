"""
REALCITY.cz scraper – inzertní portál vydavatelství Realcity (Nette, SSR HTML).

Strategie: httpx + BeautifulSoup. Web umí filtrovat po okresech
(/prodej-domu/znojmo-79, /prodej-domu/brno-venkov-69, /prodej-domu/brno-mesto-68),
takže stahujeme jen okresy Znojmo, Brno-venkov a Brno-město – domy, byty
a pozemky na prodej (9 výpisů). Výpis: `?list-perPage=100` (max), další
stránky `&list-page=N`; položky `div.media.advertise.item[data-advertise=ID]`,
celkem hlásí „Nalezeno <span class='bold highlight'>N</span>“, další stránka
= `<link rel="next">`. Detail `/nemovitost/<slug>-<id>`: h1 (.title + .address),
`.pricing .price-amount` („dohodou“ / „informace v RK“ = bez ceny), parametry
`.description .list-group-item` (label/value: Užitná plocha, Plocha pozemku,
Dispozice, Typ konstrukce, Stav nemovitosti…), popis `<h2>Popis</h2><p>`,
galerie `.gallery a.photo[href]` (media.realcity.cz/files/resized/…), GPS z
Google Maps iframe (jen když inzerát má přesnou adresu), okres + obec
z breadcrumbs (kraj → okres → obec), záloha `dataLayer` attributes (okres slug).
Web nemá ld+json pro inzerát (jen Organization) ani __NEXT_DATA__.

Proč: 30. 9. 2026 měl REALCITY v JMK 176 domů / 150 bytů / 249 pozemků
(v našich okresech 101 domů), inzerují sem hlavně menší RK (Coloseum,
Swiss Life Select…), které nejsou na Sreality. robots.txt zakazuje jen /oblibene.
"""
import asyncio
import json
import logging
import re
import time
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://www.realcity.cz"
PER_PAGE = 100  # maximum, které web nabízí (20/30/100)

# (cesta výpisu, typ nemovitosti, typ nabídky, okres) – okresní slugy mají
# pevné číselné ID (znojmo-79, brno-venkov-69, brno-mesto-68).
DEFAULT_LISTS: List[Tuple[str, str, str, str]] = [
    ("/prodej-domu/znojmo-79", "Dům", "Prodej", "Znojmo"),
    ("/prodej-bytu/znojmo-79", "Byt", "Prodej", "Znojmo"),
    ("/prodej-pozemku/znojmo-79", "Pozemek", "Prodej", "Znojmo"),
    ("/prodej-domu/brno-venkov-69", "Dům", "Prodej", "Brno-venkov"),
    ("/prodej-bytu/brno-venkov-69", "Byt", "Prodej", "Brno-venkov"),
    ("/prodej-pozemku/brno-venkov-69", "Pozemek", "Prodej", "Brno-venkov"),
    ("/prodej-domu/brno-mesto-68", "Dům", "Prodej", "Brno-město"),
    ("/prodej-bytu/brno-mesto-68", "Byt", "Prodej", "Brno-město"),
    ("/prodej-pozemku/brno-mesto-68", "Pozemek", "Prodej", "Brno-město"),
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

# Kategorie z breadcrumbs (/prodej-domu, /pronajem-bytu…) → typ nemovitosti
CATEGORY_MAP = {
    "domu": "Dům", "bytu": "Byt", "pozemku": "Pozemek",
    "rekreacnich-objektu": "Chata", "komercnich-objektu": "Komerční",
    "cinzovnich-domu": "Komerční", "historickych-objektu": "Ostatní",
    "garazi": "Ostatní",
}
OFFER_MAP = {"prodej": "Prodej", "pronajem": "Pronájem", "drazba": "Dražba"}

# Slug okresu z dataLayer (záloha, když breadcrumbs chybí) → název okresu
DISTRICT_SLUG_MAP = {
    "znojmo": "Znojmo", "brno-venkov": "Brno-venkov", "brno-mesto": "Brno-město",
    "breclav": "Břeclav", "blansko": "Blansko", "hodonin": "Hodonín", "vyskov": "Vyškov",
}


class RealcityScraper:
    """Scraper pro realcity.cz (SSR HTML, okresní výpisy)."""

    SOURCE_CODE = "REALCITY"

    def __init__(self, lists: Optional[List[Tuple[str, str, str, str]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        # 100 položek na stránku – okresní výpisy mají desítky inzerátů,
        # incremental stačí 2 stránky, full rescan 10 (= 1000 inzerátů na výpis).
        max_pages = 10 if full_rescan else 2
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 2) -> int:
        logger.info("Starting REALCITY scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                for path, property_type, offer_type, district in self.lists:
                    await self._scrape_list(path, property_type, offer_type, district, max_pages, metrics)
        self._http_client = None
        logger.info("REALCITY scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    @staticmethod
    def list_url(path: str, page: int = 1) -> str:
        query = f"list-perPage={PER_PAGE}&list-sort=updated-desc"
        if page > 1:
            query = f"list-page={page}&{query}"
        return urljoin(BASE_URL, f"{path}?{query}")

    async def _scrape_list(self, path: str, property_type: str, offer_type: str, district: str,
                           max_pages: int, metrics: Any) -> None:
        page = 1
        while page <= max_pages:
            url = self.list_url(path, page)
            try:
                with timer(f"Fetch list {path} page {page}"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
                items, total, has_next = self.parse_list_page(html)
                if not items:
                    logger.info("No items on %s page %s, stopping", path, page)
                    break
                logger.info("%s page %s: %s listings (total %s)", path, page, len(items), total)
                for item in items:
                    try:
                        detail_html = await self._fetch(item["url"])
                        normalized = self.parse_detail_page(detail_html, item, property_type, offer_type, district)
                        await self._save_listing(normalized)
                        self.scraped_count += 1
                        metrics.increment_scraped()
                        await asyncio.sleep(0.4)
                    except Exception as exc:
                        logger.error("Error processing %s: %s", item.get("url"), exc)
                        metrics.increment_failed()
                if not has_next or (total and page * PER_PAGE >= total):
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

    # ── parsování (čisté funkce, testovatelné na uloženém HTML) ──────────────

    @staticmethod
    def _parse_number(text: Optional[str]) -> Optional[float]:
        """„3 493 280 Kč“ → 3493280.0; „dohodou“ / „informace v RK“ → None."""
        if not text:
            return None
        m = re.search(r"(\d[\d\s\xa0]*)", text)
        if not m:
            return None
        try:
            return float(m.group(1).replace(" ", "").replace("\xa0", ""))
        except ValueError:
            return None

    @staticmethod
    def _abs_url(src: Optional[str]) -> Optional[str]:
        if not src:
            return None
        if src.startswith("//"):
            return "https:" + src
        return urljoin(BASE_URL, src)

    def parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], int, bool]:
        """Vrátí (položky, celkem nalezeno, existuje další stránka)."""
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen: set = set()
        for box in soup.select("div.advertise.item[data-advertise]"):
            external_id = str(box.get("data-advertise") or "").strip()
            link = box.select_one(".title a[href]") or box.select_one("a.image[href]")
            if not external_id or not link or external_id in seen:
                continue
            href = link.get("href", "")
            if "/nemovitost/" not in href:
                continue
            seen.add(external_id)
            addr = box.select_one(".address")
            price_el = box.select_one(".price .highlight") or box.select_one(".price")
            desc = box.select_one(".media-body .description")
            thumb = box.select_one("a.image img")
            results.append({
                "external_id": external_id,
                "url": urljoin(BASE_URL, href),
                "title": re.sub(r"\s+", " ", link.get_text(" ", strip=True))[:200],
                "municipality": re.sub(r"\s+", " ", addr.get_text(" ", strip=True))[:100] if addr else "",
                "price_text": price_el.get_text(" ", strip=True) if price_el else "",
                "short_description": re.sub(r"\s+", " ", desc.get_text(" ", strip=True)) if desc else "",
                "thumb": self._abs_url(thumb.get("src")) if thumb else None,
            })

        total = 0
        m = re.search(r"Nalezeno\s*<span[^>]*>\s*(\d[\d\s\xa0]*)\s*</span>", html)
        if m:
            total = int(re.sub(r"\D", "", m.group(1)) or 0)
        has_next = soup.find("link", attrs={"rel": "next"}) is not None
        return results, total, has_next

    @staticmethod
    def _data_layer_attributes(html: str) -> Dict[str, Any]:
        """`'attributes' : {...}` z inline dataLayer skriptu (okres/mesto/disposition…)."""
        m = re.search(r"'attributes'\s*:\s*(\{.*?\})\s*,\s*\n", html)
        if not m:
            return {}
        try:
            data = json.loads(m.group(1))
            return data if isinstance(data, dict) else {}
        except ValueError:
            return {}

    def parse_detail_page(self, html: str, item: Dict[str, Any], property_type: str, offer_type: str,
                          district: str) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        attrs = self._data_layer_attributes(html)

        h1 = soup.find("h1")
        h1_title = h1.select_one(".title") if h1 else None
        h1_addr = h1.select_one(".address") if h1 else None
        title = re.sub(r"\s+", " ", h1_title.get_text(" ", strip=True)) if h1_title else ""
        address = re.sub(r"\s+", " ", h1_addr.get_text(" ", strip=True)) if h1_addr else ""
        if not title and h1:
            title = re.sub(r"\s+", " ", h1.get_text(" ", strip=True))
        if not title:
            title = item.get("title", "")
        full_title = f"{title}, {address}" if address and address not in title else title

        # breadcrumbs: Vyhledat / prodej / dům / Jihomoravský kraj / Znojmo / Suchohrdly [/ 2+1]
        crumbs = [(a.get("href") or "", a.get_text(" ", strip=True)) for a in soup.select("ol.breadcrumb a")]
        ptype, otype = property_type, offer_type
        for href, _ in crumbs:
            m = re.fullmatch(r"/(prodej|pronajem|drazba)(?:-([a-z-]+))?", href)
            if m:
                otype = OFFER_MAP.get(m.group(1), otype)
                if m.group(2):
                    ptype = CATEGORY_MAP.get(m.group(2), ptype)
        names = [name for _, name in crumbs]
        kraj_idx = next((i for i, n in enumerate(names) if n.lower().endswith("kraj")), None)
        detail_district = names[kraj_idx + 1] if kraj_idx is not None and len(names) > kraj_idx + 1 else ""
        municipality = names[kraj_idx + 2] if kraj_idx is not None and len(names) > kraj_idx + 2 else ""
        if not detail_district:
            detail_district = DISTRICT_SLUG_MAP.get(str(attrs.get("okres") or "").lower(), "")
        district = detail_district or district
        if not municipality:
            municipality = (item.get("municipality") or attrs.get("short_locality") or "").split(",")[0].strip()
        location_text = ", ".join(x for x in (address or municipality, f"okres {district}" if district else "") if x)

        price_el = soup.select_one(".pricing .price-amount") or soup.select_one(".pricing .list-group-item-value")
        price = self._parse_number(price_el.get_text(" ", strip=True) if price_el else "")
        if price is None:
            price = self._parse_number(item.get("price_text", ""))

        params: Dict[str, str] = {}
        for li in soup.select(".description .list-group-item"):
            label = li.select_one(".list-group-item-label")
            value = li.select_one(".list-group-item-value")
            if label and value:
                key = label.get_text(" ", strip=True).lower()
                if key:
                    params[key] = re.sub(r"\s+", " ", value.get_text(" ", strip=True))

        area_land = self._parse_number(params.get("plocha pozemku", ""))
        area_built = self._parse_number(params.get("užitná plocha", "") or params.get("celková plocha", "")
                                        or params.get("zastavěná plocha", "") or params.get("podlahová plocha", ""))
        if area_built is None and attrs.get("usable_area"):
            area_built = float(attrs["usable_area"])
        if ptype == "Pozemek":
            if area_land is None:
                area_land = area_built
            area_built = None

        disposition = params.get("dispozice") or str(attrs.get("disposition") or "").replace("-", "+") or None
        rooms: Optional[int] = None
        if disposition:
            m = re.match(r"(\d+)\s*\+", disposition)
            rooms = int(m.group(1)) if m else None

        description = ""
        popis = next((h2 for h2 in soup.select(".detail.advertise h2") if h2.get_text(strip=True).lower() == "popis"), None)
        if popis:
            p = popis.find_next("p")
            description = p.get_text("\n", strip=True) if p else ""
        if not description:
            description = item.get("short_description") or str(attrs.get("short_description") or "")
        agency = soup.select_one(".contact.agency .agency-name")
        if agency:
            description = (description + f"\n\nRealitní kancelář: {agency.get_text(' ', strip=True)}").strip()

        photos: List[str] = []
        for a in soup.select(".gallery a.photo[href]"):
            src = self._abs_url(a.get("href"))
            if src and src not in photos:
                photos.append(src)
        if not photos and item.get("thumb"):
            photos.append(item["thumb"])

        external_id = item.get("external_id") or ""
        m = re.search(r"-(\d+)/?$", item.get("url", ""))
        if not external_id and m:
            external_id = m.group(1)

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": str(external_id),
            "url": item["url"],
            "title": full_title[:200],
            "description": description[:5000],
            "property_type": ptype,
            "offer_type": otype,
            "price": price,
            "location_text": location_text[:200] or f"okres {district}",
            "municipality": municipality[:100] or None,
            "district": district or None,
            "area_built_up": area_built,
            "area_land": area_land,
            "rooms": rooms,
            "disposition": disposition,
            "condition": params.get("stav nemovitosti") or None,
            "construction_type": params.get("typ konstrukce") or None,
            "photos": photos[:50],
        }
        m = re.search(r"maps/embed/v1/place\?q=(-?\d+\.\d+)%2C(?:%20)?(-?\d+\.\d+)", html)
        if m:
            result["latitude"], result["longitude"] = float(m.group(1)), float(m.group(2))
        published = attrs.get("publishedDateTime")
        if published:
            try:
                result["date_created_source"] = datetime.fromisoformat(str(published))
            except ValueError:
                pass
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
