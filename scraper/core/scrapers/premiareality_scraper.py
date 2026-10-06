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
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://www.premiareality.cz"

CATEGORIES = [
    ("byty",    "Byt"),
    ("domy",    "Dům"),
    ("parcely", "Pozemek"),
    ("rekreace","Pozemek"),  # na webu jsou tu zahrady (chaty má realitka mezi domy)
    ("ostatni", "Ostatní"),
]

# Slug okresu v URL detailu („…-okres-znojmo-…") → název okresu. Víceslovné okresy musí být
# vyjmenované, jinak by z „-okres-brno-venkov-dum-…" nešlo poznat, kde okres končí.
DISTRICT_SLUGS = {
    "znojmo": "Znojmo", "brno-venkov": "Brno-venkov", "brno-mesto": "Brno-město",
    "trebic": "Třebíč", "breclav": "Břeclav", "hodonin": "Hodonín", "vyskov": "Vyškov",
    "blansko": "Blansko", "jihlava": "Jihlava", "zdar-nad-sazavou": "Žďár nad Sázavou",
    "ceska-lipa": "Česká Lípa", "cesky-krumlov": "Český Krumlov", "ceske-budejovice": "České Budějovice",
    "usti-nad-orlici": "Ústí nad Orlicí", "usti-nad-labem": "Ústí nad Labem", "jicin": "Jičín",
    "louny": "Louny", "jindrichuv-hradec": "Jindřichův Hradec", "havlickuv-brod": "Havlíčkův Brod",
    "pelhrimov": "Pelhřimov", "prostejov": "Prostějov", "olomouc": "Olomouc",
}

# Typ podle řádku tabulky („Nemovitost" u domů, „Typ nemovitosti" u bytů, „Podtyp nemovitosti"
# u zahrad a komerčních objektů). Pořadí rozhoduje – první shoda vyhrává.
_TYPE_LABEL_PATTERNS = [
    (re.compile(r"\bbyt"), "Byt"),
    (re.compile(r"d[ůu]m|\bvil[ay]|chat[ay]|chalup"), "Dům"),
    (re.compile(r"pozem|parcel|zahrad|\bpole\b|\bles\b|\blouk|\bsad|vinic"), "Pozemek"),
    (re.compile(r"gar[áa][žz]"), "Garáž"),
    (re.compile(r"kancel|sklad|obchod|v[ýy]rob|komer[čc]|restaur|ubytov"), "Komerční"),
    (re.compile(r"sklep"), "Ostatní"),
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
                                if normalized.pop("sold", False):
                                    # Realitka nechává prodané nabídky na webu – u nás aktivní být nesmí
                                    logger.info("Listing %s is sold – deactivating", normalized["external_id"])
                                    await get_db_manager().deactivate_listing(self.SOURCE_CODE, normalized["external_id"])
                                    metrics.increment_scraped()
                                    await asyncio.sleep(0.4)
                                    continue
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

    @http_retry
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

    @staticmethod
    def _listing_status(soup: BeautifulSoup) -> Optional[str]:
        """„sold" / „reserved" podle štítku v tabulce parametrů (<td class="prodano|rezervace">)."""
        if soup.select_one("td.prodano"):
            return "sold"
        if soup.select_one("td.rezervace"):
            return "reserved"
        return None

    @staticmethod
    def _district_from_url(url: str) -> Optional[str]:
        """Okres ze slugu detailu: „…-strachotice-okres-znojmo-dum-3-1-na-prodej-3164.html" → „Znojmo".
        Nabídka bez okresu ve slugu (Praha) vrací None."""
        match = re.search(r"-okres-([a-z-]+)", url.lower())
        if not match:
            return None
        rest = match.group(1)
        # Nejdelší známý slug má přednost („brno-venkov" před čímkoli kratším)
        for slug in sorted(DISTRICT_SLUGS, key=len, reverse=True):
            if rest == slug or rest.startswith(slug + "-"):
                return DISTRICT_SLUGS[slug]
        first = rest.split("-")[0]
        return first.title() if first else None

    @staticmethod
    def _property_type(params: Dict[str, str], url: str, default_type: str) -> str:
        """Typ nemovitosti z tabulky parametrů a kategorie v URL.

        Web typ uvádí pod třemi různými popisky: „Nemovitost" (domy), „Typ nemovitosti" (byty)
        a „Podtyp nemovitosti" (zahrady, pozemky, komerční objekty). Dřív se četl jen první,
        takže zahrady z /rekreace/ dostaly výchozí typ kategorie „Dům" a pletly se mezi domy.
        """
        category_match = re.search(r"premiareality\.cz/([a-z]+)/", url.lower())
        category = category_match.group(1) if category_match else ""
        # U pozemků říká podtyp jen účel („Bydlení", „Komerční", „Pole") – typ je vždy pozemek
        if category == "parcely":
            return "Pozemek"
        label = (
            params.get("nemovitost")
            or params.get("typ nemovitosti")
            or params.get("podtyp nemovitosti")
            or ""
        ).lower()
        for pattern, property_type in _TYPE_LABEL_PATTERNS:
            if pattern.search(label):
                return property_type
        return default_type

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

        # Stav nabídky: web místo řádku s cenou ukáže „PRODÁNO" nebo „REZERVACE" a inzerát nechá viset.
        # 6. 10. 2026 bylo z 51 „aktivních" nabídek 19 prodaných a 21 rezervovaných (Horní Leska).
        status = self._listing_status(soup)
        if status == "sold":
            result["sold"] = True
        elif status == "reserved":
            # Rezervace občas padne – nabídka zůstává vidět, se štítkem a poslední známou cenou
            result["price_note"] = "Rezervace"
            result["keep_last_price"] = True
        elif result["price"] is None and price_text:
            result["price_note"] = price_text[:200]  # „Informace o ceně v RK"

        # Typ nemovitosti
        result["property_type"] = self._property_type(
            params, list_item["url"], list_item.get("default_property_type", "Ostatní")
        )

        # Plochy
        uzitna = params.get("užitná plocha", params.get("uzitna plocha", ""))
        if uzitna:
            result["area_built_up"] = self._parse_area(uzitna)

        zahrada = params.get("plocha zahrady", params.get("plocha pozemku", params.get("plocha parcely", "")))
        if zahrada:
            result["area_land"] = self._parse_area(zahrada)

        # Lokace: nabídka s ulicí má řádky „Ulice" + „Město", bez ulice jen „Obec"
        # („Lokace" je charakter místa – „Klidná část obce" –, ne adresa).
        ulice = params.get("ulice", "").strip()
        mesto = (params.get("město") or params.get("mesto") or params.get("obec") or "").strip()
        if not mesto:
            # Záloha z podtitulku („Na Hrázi - Znojmo" nebo jen „Strachotice")
            h2 = soup.find("h2")
            h2_text = h2.get_text(" ", strip=True) if h2 else ""
            if " - " in h2_text and not ulice:
                ulice, mesto = (part.strip() for part in h2_text.rsplit(" - ", 1))
            else:
                mesto = h2_text.strip()
        if ulice and mesto and ulice != mesto:
            result["location_text"] = f"{ulice}, {mesto}"[:200]
        elif mesto or ulice:
            result["location_text"] = (mesto or ulice)[:200]
        else:
            result["location_text"] = "Znojmo a okolí"
        if mesto:
            result["municipality"] = mesto[:100]

        # Okres: v obsahu stránky není, jen ve slugu URL („…-okres-znojmo-…"). Bez něj viděl
        # geografický filtr jen název obce („Strachotice") a 6. 10. 2026 tak chybělo 6 z 8 volných
        # domů na Znojemsku – prošly jen ty, které mají „Znojmo" přímo v adrese.
        district = self._district_from_url(list_item["url"])
        if district:
            result["district"] = district

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
            photo_url = ""
            if parent_a:
                photo_href = parent_a.get("href", "")
                candidate = urljoin(BASE_URL, photo_href) if photo_href else ""
                # Přeskoč .html hrefs (odkazují na stránku inzerátu, ne na fotku)
                if candidate.endswith(".html") or not re.search(r'\.(jpe?g|png|webp|gif)(\?|$)', candidate, re.IGNORECASE):
                    candidate = ""
                if candidate:
                    photo_url = self._fix_thumbs_url(candidate)
            if not photo_url:
                src = img.get("src") or img.get("data-src", "")
                if src:
                    photo_url = self._fix_thumbs_url(urljoin(BASE_URL, src))

            if photo_url and photo_url not in seen_photos:
                seen_photos.add(photo_url)
                photo_urls.append(photo_url)

        # Fallback: všechny imgs přes 5000 pixelů (podle pattern z průzkumu)
        if not photo_urls:
            for img in soup.find_all("img"):
                src = str(img.get("src", "") or img.get("data-src", ""))
                if "importestate" in src or "estate" in src:
                    photo_url = self._fix_thumbs_url(urljoin(BASE_URL, src))
                    if photo_url not in seen_photos:
                        seen_photos.add(photo_url)
                        photo_urls.append(photo_url)

        result["photos"] = photo_urls[:50]

        return result

    @staticmethod
    def _fix_thumbs_url(url: str) -> str:
        """
        Opraví URL fotek PREMIAREALITY, které chybně chybí thumbs_NNN_NNN/ podadresář.

        Vstup:  /files/importestate/{id}/1920x1920wm..._thumb1300x866.jpg
        Výstup: /files/importestate/{id}/thumbs_1300_866/1920x1920wm..._thumb1300x866.jpg

        Pokud URL již thumbs_ dir obsahuje nebo neodpovídá vzoru, vrátí beze změny.
        """
        if "/thumbs_" in url:
            return url  # Už v pořádku
        m = re.search(
            r'(/files/importestate/\d+/)([^/]+_thumb(\d+)x(\d+)\.[a-zA-Z]+)$',
            url,
        )
        if m:
            prefix = m.group(1)
            filename = m.group(2)
            w, h = m.group(3), m.group(4)
            base = url[: m.start()]
            return f"{base}{prefix}thumbs_{w}_{h}/{filename}"
        return url

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
