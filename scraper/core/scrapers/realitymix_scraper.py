"""
RealityMIX.cz scraper – celostátní realitní portál (Internet Info / DALTEN media).

Strategie: httpx + BeautifulSoup, SSR HTML. Výpis umí filtrovat po okresech
(/reality/domy/prodej/jihomoravsky/znojmo, …/brno-venkov, …/brno-mesto; stejně
byty a pozemky), stránkuje ?stranka=N po 20 položkách, počet je v textu
„Zobrazujeme výsledky 1-20 z celkem 163 nalezených“. Každá stránka obsahuje
i 1 topovanou nabídku odjinud (např. okr. Vyškov) – parsujeme ji taky, okres
si ale bereme z detailu, takže ji DB filtr (target_districts) odmítne sám.
Odkazy /trackredir/N ve výpisu jsou kontakty (telefon, WhatsApp), ne inzeráty.

Detail: ld+json BreadcrumbList (typ, nabídka, kraj, OKRES, obec), og:title
(popisný titulek od RK), h1 „Prodej domu/vily, 124 m²“, adresa
.advert-detail-heading__address, cena .advert-detail-heading__price-value,
parametry ul.detail-information__data (Užitná plocha, Plocha parcely, Stav
objektu, Druh objektu, Dispozice bytu…), popis .advert-description__text-inner-inner,
GPS div#print-map[data-gps-lat|lon], fotky st.realitymix.cz/i/<rk>/<id>/nab_<n>.jpg
(bez přípony _nahled/_detail = plná velikost; filtrujeme podle <id>, protože
v HTML jsou i náhledy „podobných nemovitostí“).

Proč: 30. 9. 2026 měl RealityMIX 844 domů v JMK (163 v okrese Znojmo),
robots.txt scraping výpisů i detailů povoluje.
"""
import asyncio
import json
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

BASE_URL = "https://realitymix.cz"
PAGE_SIZE = 20

# (cesta výpisu, typ nemovitosti, typ nabídky, okres, který výpis pokrývá).
# Brno-město jen byty (domy do 10 mil. Kč tam prakticky nejsou, pozemky nad filtr).
DEFAULT_LISTS: List[Tuple[str, str, str, str]] = [
    ("/reality/domy/prodej/jihomoravsky/znojmo", "Dům", "Prodej", "Znojmo"),
    ("/reality/byty/prodej/jihomoravsky/znojmo", "Byt", "Prodej", "Znojmo"),
    ("/reality/pozemky/prodej/jihomoravsky/znojmo", "Pozemek", "Prodej", "Znojmo"),
    ("/reality/domy/prodej/jihomoravsky/brno-venkov", "Dům", "Prodej", "Brno-venkov"),
    ("/reality/byty/prodej/jihomoravsky/brno-venkov", "Byt", "Prodej", "Brno-venkov"),
    ("/reality/pozemky/prodej/jihomoravsky/brno-venkov", "Pozemek", "Prodej", "Brno-venkov"),
    ("/reality/byty/prodej/jihomoravsky/brno-mesto", "Byt", "Prodej", "Brno-město"),
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

# Druhá položka breadcrumbs (sekce webu) → typ nemovitosti
SECTION_MAP = {"Domy": "Dům", "Byty": "Byt", "Pozemky": "Pozemek", "Komerční": "Komerční",
               "Ostatní": "Ostatní", "Projekty": "Ostatní"}
OFFER_MAP = {"Prodej": "Prodej", "Pronájem": "Pronájem", "Dražba": "Dražba", "Dražby": "Dražba"}
CONSTRUCTION_MAP = {"cihlová": "Cihla", "cihla": "Cihla", "panelová": "Panel", "panel": "Panel",
                    "dřevěná": "Dřevostavba", "dřevostavba": "Dřevostavba", "smíšená": "Smíšená",
                    "kamenná": "Kámen", "montovaná": "Montovaná", "skeletová": "Skelet"}
# Statutární města bez „okr.“ v adrese → okres (fallback, když chybí breadcrumbs)
CITY_DISTRICT_MAP = {"Brno": "Brno-město", "Znojmo": "Znojmo"}

DETAIL_ID_RE = re.compile(r"-(\d{5,})\.html(?:[#?].*)?$")
NUMBER_RE = re.compile(r"(\d[\d\s\xa0]*(?:[.,]\d+)?)")
DISPOSITION_RE = re.compile(r"\b(\d)\s*\+\s*(kk|\d)\b", re.I)


class RealityMixScraper:
    """Scraper pro realitymix.cz (SSR HTML, okresní výpisy)."""

    SOURCE_CODE = "REALITYMIX"

    def __init__(self, lists: Optional[List[Tuple[str, str, str, str]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 40 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting RealityMIX scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                for path, property_type, offer_type, district in self.lists:
                    await self._scrape_list(path, property_type, offer_type, district, max_pages, metrics)
        self._http_client = None
        logger.info("RealityMIX scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    async def _scrape_list(self, path: str, property_type: str, offer_type: str, district: str,
                           max_pages: int, metrics: Any) -> None:
        page = 1
        while page <= max_pages:
            url = urljoin(BASE_URL, path if page == 1 else f"{path}?stranka={page}")
            try:
                with timer(f"Fetch list {path} page {page}"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
                items, total, has_next = self.parse_list_page(html, page)
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
                if not has_next or (total and page * PAGE_SIZE >= total):
                    break
                page += 1
                await asyncio.sleep(1.0)
            except httpx.HTTPStatusError as exc:
                # ?stranka=N za poslední stránkou vrací 404
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

    def parse_list_page(self, html: str, page: int = 1) -> Tuple[List[Dict[str, Any]], int, bool]:
        """Vrátí (položky, celkový počet nalezených, existuje další stránka)."""
        soup = BeautifulSoup(html, "html.parser")
        container = soup.select_one("ul[data-list-container]") or soup
        results: List[Dict[str, Any]] = []
        seen: set = set()
        for li in container.select("li.advert-item"):
            link = li.select_one("h2 a[href*='/detail/']") or li.select_one("a[href*='/detail/']")
            if not link:
                continue
            href = link.get("href", "").split("#")[0]
            m = DETAIL_ID_RE.search(href)
            if not m or href in seen:
                continue
            seen.add(href)
            addr = li.select_one(".advert-item__content-data p")
            price_el = next((sp for sp in li.select(".advert-item__content-data div.font-extrabold > span")
                             if re.search(r"Kč|dohodou", sp.get_text())), None)
            results.append({
                "url": urljoin(BASE_URL, href),
                "external_id": m.group(1),
                "title": re.sub(r"\s+", " ", link.get_text(" ", strip=True))[:200],
                "address": addr.get_text(" ", strip=True).replace("\xa0", " ") if addr else "",
                "price_text": price_el.get_text(" ", strip=True).replace("\xa0", " ") if price_el else "",
            })

        total = 0
        m_total = re.search(r"z\s+celkem\s+(\d[\d\s\xa0]*)\s+nalezen", soup.get_text(" ", strip=True))
        if m_total:
            total = int(re.sub(r"\D", "", m_total.group(1)) or 0)
        has_next = any(f"stranka={page + 1}" in (a.get("href") or "") for a in soup.select("a[href*='stranka=']"))
        return results, total, has_next

    @staticmethod
    def _parse_number(text: Optional[str]) -> Optional[float]:
        if not text:
            return None
        m = NUMBER_RE.search(text)
        if not m:
            return None
        try:
            return float(m.group(1).replace(" ", "").replace("\xa0", "").replace(",", "."))
        except ValueError:
            return None

    @staticmethod
    def _breadcrumbs(soup: BeautifulSoup) -> List[str]:
        for script in soup.find_all("script", type="application/ld+json"):
            try:
                data = json.loads(script.get_text())
            except (ValueError, TypeError):
                continue
            if isinstance(data, dict) and data.get("@type") == "BreadcrumbList":
                names = []
                for el in data.get("itemListElement", []):
                    item = el.get("item") if isinstance(el, dict) else None
                    name = (item or {}).get("name") if isinstance(item, dict) else el.get("name")
                    if name:
                        names.append(str(name).strip())
                return names
        return []

    @staticmethod
    def _split_address(address: str) -> Tuple[str, Optional[str]]:
        """'Ulice, Obec, okr. Znojmo' → ('Obec', 'Znojmo'); 'Smutného, Znojmo' → ('Znojmo', None)."""
        parts = [p.strip() for p in address.split(",") if p.strip()]
        district: Optional[str] = None
        if parts and re.match(r"^okr(es|\.)\s+", parts[-1], re.I):
            district = re.sub(r"^okr(es|\.)\s+", "", parts[-1], flags=re.I).strip()
            parts = parts[:-1]
        municipality = parts[-1] if parts else ""
        return municipality, district

    def parse_detail_page(self, html: str, item: Dict[str, Any], property_type: str, offer_type: str,
                          district: str) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        crumbs = self._breadcrumbs(soup)

        # Breadcrumbs: [RealityMix, Byty, Prodej, (3+kk|les), Jihomoravský kraj, OKRES, Obec, část]
        ptype = SECTION_MAP.get(crumbs[1], property_type) if len(crumbs) > 1 else property_type
        otype = OFFER_MAP.get(crumbs[2], offer_type) if len(crumbs) > 2 else offer_type
        kraj_idx = next((i for i, c in enumerate(crumbs) if c.lower().endswith("kraj") or c.startswith("Hlavní město")), -1)
        crumb_district = crumbs[kraj_idx + 1] if 0 <= kraj_idx and kraj_idx + 1 < len(crumbs) else None
        crumb_municipality = crumbs[kraj_idx + 2] if 0 <= kraj_idx and kraj_idx + 2 < len(crumbs) else None

        h1 = soup.find("h1")
        h1_text = re.sub(r"\s+", " ", h1.get_text(" ", strip=True)) if h1 else ""
        og_title = soup.find("meta", property="og:title")
        title = (og_title.get("content") if og_title else "") or h1_text or item.get("title", "")
        title = re.sub(r"\s+", " ", title).strip()[:200]

        addr_el = soup.select_one(".advert-detail-heading__address")
        address = addr_el.get_text(" ", strip=True) if addr_el else item.get("address", "")
        map_el = soup.find(attrs={"data-gps-lat": True})
        map_address = map_el.get("data-address", "") if map_el else ""
        addr_municipality, addr_district = self._split_address(map_address or address)
        if not addr_district and not map_address:
            _, addr_district = self._split_address(address)
        municipality = crumb_municipality or addr_municipality or ""
        final_district = crumb_district or addr_district or CITY_DISTRICT_MAP.get(municipality) or district

        lower_title = f"{title} {h1_text}".lower()
        if ptype == "Dům" and re.search(r"\bchat|\bchalup|rekrea", lower_title):
            ptype = "Chata"
        if "dražb" in lower_title:
            otype = "Dražba"

        params: Dict[str, str] = {}
        for li in soup.select("ul.detail-information__data li"):
            spans = li.find_all("span")
            if len(spans) >= 2:
                params[spans[0].get_text(" ", strip=True).rstrip(":").lower()] = spans[1].get_text(" ", strip=True)
        for tr in soup.select("table tr"):
            tds = tr.find_all("td")
            if len(tds) >= 2:
                key = tds[0].get_text(" ", strip=True).rstrip(":").lower()
                params.setdefault(key, tds[1].get_text(" ", strip=True))

        price_el = soup.select_one(".advert-detail-heading__price-value")
        price = self._parse_number(price_el.get_text(" ", strip=True) if price_el else None) \
            or self._parse_number(params.get("cena")) or self._parse_number(item.get("price_text"))
        if price is not None and price < 1000:
            price = None  # „Cena: dohodou“ / „1 Kč“ apod.

        area_land = self._parse_number(params.get("plocha parcely") or params.get("plocha pozemku"))
        area_built = self._parse_number(params.get("užitná plocha") or params.get("celková podlahová plocha")
                                        or params.get("podlahová plocha") or params.get("zastavěná plocha"))
        if area_built is None:
            m_area = re.search(r"(\d[\d\s\xa0]*)\s*m²", h1_text)
            area_built = self._parse_number(m_area.group(1)) if m_area else None
        if ptype == "Pozemek":
            area_land = area_land or self._parse_number(params.get("celková plocha") or params.get("plocha")) or area_built
            area_built = None

        desc_el = soup.select_one(".advert-description__text-inner-inner") or soup.select_one(".advert-description__text-inner")
        description = desc_el.get_text("\n", strip=True) if desc_el else ""
        rk_name = next((a.get_text(" ", strip=True) for a in soup.select("a[href*='detail-realitni-kancelare']")
                        if a.get_text(strip=True)), "")
        if rk_name:
            description = (description + f"\n\nRealitní kancelář: {rk_name[:100]}").strip()

        disposition: Optional[str] = None
        m_disp = DISPOSITION_RE.search(params.get("dispozice bytu", "") or params.get("dispozice", "") or h1_text)
        if not m_disp and ptype in ("Dům", "Chata"):
            # u domů RK dispozici do parametrů nedává, bývá jen v popisu („o dispozici 3+1“)
            m_disp = re.search(r"dispozic\w*\s+(\d)\s*\+\s*(kk|\d)\b", description, re.I)
        if m_disp:
            disposition = f"{m_disp.group(1)}+{m_disp.group(2).lower()}"
        rooms = int(disposition.split("+")[0]) if disposition else None

        external_id = item.get("external_id") or (DETAIL_ID_RE.search(item.get("url", "")) or [None, ""])[1]
        photos: List[str] = []
        for src in re.findall(rf"https?://st\.realitymix\.cz/i/\d+/{external_id}/nab_\d+\.jpe?g", html):
            src = src.replace("http://", "https://")
            if src not in photos:
                photos.append(src)
        if not photos:
            og_image = soup.find("meta", property="og:image")
            if og_image and og_image.get("content"):
                photos.append(og_image["content"].replace("http://", "https://"))

        location_text = f"{municipality}, okres {final_district}" if municipality else f"okres {final_district}"

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": str(external_id),
            "url": item["url"],
            "title": title,
            "description": description[:5000],
            "property_type": ptype,
            "offer_type": otype,
            "price": price,
            "location_text": location_text[:200],
            "municipality": municipality[:100] or None,
            "district": final_district,
            "area_built_up": area_built,
            "area_land": area_land,
            "rooms": rooms,
            "disposition": disposition,
            "condition": (params.get("stav objektu") or None),
            "construction_type": CONSTRUCTION_MAP.get((params.get("druh objektu") or "").lower()),
            "photos": photos[:50],
        }
        if map_el:
            try:
                result["latitude"] = float(map_el["data-gps-lat"])
                result["longitude"] = float(map_el["data-gps-lon"])
            except (KeyError, TypeError, ValueError):
                pass
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
