"""
CENTURY 21 Czech Republic scraper – century21.cz
Největší realitní síť v ČR, region: Jihomoravský kraj / Znojemsko
SSR (server-side rendered) – httpx + BeautifulSoup

Scrapuje všechny typy nemovitostí (domy, byty, pozemky, ostatní)
pro region okresu Znojmo s podporou dalších okresů přes konfiguraci.
"""
import json
import logging
import re
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.century21.cz"
SEARCH_URL = f"{BASE_URL}/nemovitosti"

HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Accept": "text/html,application/xhtml+xml,*/*;q=0.8",
}

# Znojmo region – pokrývá všechny typy nemovitostí a transakce
SEARCH_CONFIGS = [
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["HOUSE"],      "listingType": "SALE"},
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["HOUSE"],      "listingType": "RENT"},
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["FLAT"],       "listingType": "SALE"},
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["FLAT"],       "listingType": "RENT"},
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["LAND"],       "listingType": "SALE"},
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["COMMERCIAL"], "listingType": "SALE"},
    {"regions": ["Jihomoravský"], "county": ["Znojmo"], "propertyType": ["GARAGE"],     "listingType": "SALE"},
]

OFFER_TYPE_MAP = {
    "SALE": "Sale",
    "RENT": "Rent",
}

PROPERTY_TYPE_MAP = {
    "HOUSE":      "House",
    "FLAT":       "Apartment",
    "LAND":       "Land",
    "COMMERCIAL": "Commercial",
    "GARAGE":     "Garage",
}

# Mapování z tabulky na stránce
TABLE_PROPERTY_TYPE = {
    "rodinné domy":        "House",
    "rodinný dům":         "House",
    "bytový dům":          "House",
    "řadový dům":          "House",
    "vila":                "House",
    "byt":                 "Apartment",
    "byty":                "Apartment",
    "pozemek":             "Land",
    "pozemky":             "Land",
    "komerční":            "Commercial",
    "kancelář":            "Commercial",
    "sklad":               "Commercial",
    "chata":               "Cottage",
    "chalupa":             "Cottage",
    "rekreační":           "Cottage",
    "garáž":               "Garage",
    "garážové stání":      "Garage",
}


class Century21Scraper:
    SOURCE_CODE = "CENTURY21"

    def __init__(self, search_configs: Optional[List[Dict[str, Any]]] = None):
        """
        Args:
            search_configs: Seznam search konfigurací. Defaultně SEARCH_CONFIGS (Znojmo).
        """
        self.search_configs = search_configs or SEARCH_CONFIGS

    async def run(self, full_rescan: bool = False) -> int:
        logger.info(f"[{self.SOURCE_CODE}] Starting scrape (full_rescan={full_rescan})")
        try:
            return await self.scrape(max_pages_per_config=50 if full_rescan else 5)
        except Exception as e:
            logger.error(f"[{self.SOURCE_CODE}] Fatal error: {e}", exc_info=True)
            return 0

    async def scrape(self, max_pages_per_config: int = 5) -> int:
        """Iteruje přes všechny search konfigurace a stránky."""
        all_detail_urls: set = set()

        async with httpx.AsyncClient(headers=HEADERS, follow_redirects=True, timeout=30) as client:
            # Fáze 1: sběr všech URL inzerátů
            for cfg in self.search_configs:
                urls = await self._collect_urls_for_config(client, cfg, max_pages_per_config)
                logger.info(f"[{self.SOURCE_CODE}] Config {cfg.get('propertyType','?')}/{cfg.get('listingType','?')}: {len(urls)} URLs")
                all_detail_urls.update(urls)

            logger.info(f"[{self.SOURCE_CODE}] Total unique listings: {len(all_detail_urls)}")

            # Fáze 2: scraping každého detailu
            count = 0
            db = get_db_manager()
            for url in all_detail_urls:
                try:
                    # Infer offer/property type from URL for fallback
                    inferred_offer = "Sale" if "/prodej-" in url else "Rent" if "/pronajem-" in url else "Sale"
                    inferred_prop = self._property_type_from_url(url)

                    item = await self._parse_detail(client, url, inferred_offer, inferred_prop)
                    if item:
                        await db.upsert_listing(item)
                        count += 1
                        logger.debug(f"[{self.SOURCE_CODE}] Saved: {item.get('title','?')}")
                except Exception as e:
                    logger.warning(f"[{self.SOURCE_CODE}] Error parsing {url}: {e}")

        logger.info(f"[{self.SOURCE_CODE}] Done – {count} listings saved")
        return count

    # ------------------------------------------------------------------
    # Kolektování URL ze stránkování
    # ------------------------------------------------------------------

    async def _collect_urls_for_config(
        self,
        client: httpx.AsyncClient,
        cfg: Dict[str, Any],
        max_pages: int,
    ) -> List[str]:
        """Prochází stránky pro daný search config a sbírá detail URL."""
        urls: List[str] = []
        seen: set = set()

        for page in range(1, max_pages + 1):
            page_urls = await self._fetch_listing_page(client, cfg, page)
            if not page_urls:
                break  # žádné nové inzeráty = konec stránkování

            new_urls = [u for u in page_urls if u not in seen]
            if not new_urls:
                break  # duplicity = jsme na konci

            seen.update(new_urls)
            urls.extend(new_urls)

            if len(page_urls) < 12:
                break  # pravděpodobně poslední stránka (málo inzerátů)

        return urls

    async def _fetch_listing_page(
        self,
        client: httpx.AsyncClient,
        cfg: Dict[str, Any],
        page: int,
    ) -> List[str]:
        """Stáhne jednu stránku výpisu a vrátí seznam detail URL."""
        filter_data = {
            "regions": cfg.get("regions", []),
            "country": [],
            "county": cfg.get("county", []),
            "district": [],
            "propertyType": cfg.get("propertyType", []),
            "listingType": cfg.get("listingType", "SALE"),
            "isAbroad": False,
            "construction": [],
            "disposition": [],
            "condition": [],
            "ownershipType": [],
            "energy": [],
        }
        filter_json = json.dumps(filter_data, ensure_ascii=False, separators=(",", ":"))

        params = {"filter": filter_json}
        if page > 1:
            params["page"] = str(page)

        try:
            resp = await client.get(SEARCH_URL, params=params)
            resp.raise_for_status()
        except httpx.HTTPError as e:
            logger.warning(f"[{self.SOURCE_CODE}] HTTP error listing page {page}: {e}")
            return []

        soup = BeautifulSoup(resp.text, "html.parser")

        # Detekce "žádné inzeráty" stránky
        heading = soup.find(string=re.compile(r"NEMOVITOSTÍ|NEMOVITOST", re.I))
        if heading:
            # Pokud vrátí "0 NEMOVITOSTÍ", přeskočíme
            m = re.search(r"(\d+)\s+NEMOVITOST", heading.strip(), re.I)
            if m and int(m.group(1)) == 0:
                return []

        urls = []
        for a in soup.select("a[href*='/nemovitosti/']"):
            href = a.get("href", "").strip()
            if not href:
                continue
            # Detail URL musí obsahovat id= UUID
            if "id=" not in href:
                continue
            # Přeskočit navigační linky
            if href.rstrip("/") == "/nemovitosti":
                continue
            full_url = urljoin(BASE_URL, href)
            # Odstraň duplicity z galerie/video odkazů
            if full_url not in urls:
                urls.append(full_url)

        return urls

    # ------------------------------------------------------------------
    # Parsování detailní stránky
    # ------------------------------------------------------------------

    async def _parse_detail(
        self,
        client: httpx.AsyncClient,
        url: str,
        inferred_offer_type: str = "Sale",
        inferred_property_type: str = "House",
    ) -> Optional[Dict[str, Any]]:
        """Stáhne a naparsuje detail stránky inzerátu."""
        try:
            resp = await client.get(url)
            resp.raise_for_status()
        except httpx.HTTPError as e:
            logger.warning(f"[{self.SOURCE_CODE}] HTTP error detail {url}: {e}")
            return None

        soup = BeautifulSoup(resp.text, "html.parser")

        # External ID – preferuj UUID z URL, fallback na číselné ID ze stránky
        uuid_match = re.search(r"id=([0-9a-f\-]{36})", url, re.I)
        external_id = uuid_match.group(1) if uuid_match else url

        # Číselné interní ID (ID: 971693)
        numeric_id = None
        for el in soup.find_all(string=re.compile(r"^\s*ID:\s*\d+")):
            m = re.search(r"ID:\s*(\d+)", el.strip())
            if m:
                numeric_id = m.group(1)
                break

        # Titulek
        title = ""
        for tag in ["h1", "h2", "h3"]:
            h = soup.find(tag)
            if h:
                txt = h.get_text(strip=True)
                if len(txt) > 5 and "cookie" not in txt.lower():
                    title = txt
                    break

        if not title:
            logger.warning(f"[{self.SOURCE_CODE}] No title at {url}")
            return None

        # Parsování parametrové tabulky (| KATEGORIE | Rodinné domy | ...)
        params = self._parse_detail_table(soup)

        # Cena
        price = self._extract_price(soup)

        # Offer type (z URL nebo tabulky)
        offer_type = inferred_offer_type

        # Property type (z tabulky KATEGORIE nebo URL)
        property_type = inferred_property_type
        if "KATEGORIE" in params:
            cat = params["KATEGORIE"].lower()
            for key, ptype in TABLE_PROPERTY_TYPE.items():
                if key in cat:
                    property_type = ptype
                    break

        # Plocha
        area = None
        for key in ["PLOCHA UŽITNÁ", "PLOCHA", "VELIKOST BYTU"]:
            if key in params:
                m = re.search(r"(\d+(?:[.,]\d+)?)", params[key].replace("\xa0", ""))
                if m:
                    area = float(m.group(1).replace(",", "."))
                    break

        # Dispozice (VELIKOST = "4+kk")
        disposition = params.get("VELIKOST", "")

        # Lokace
        location = params.get("LOKALITA", "") or params.get("OBEC", "")
        if not location:
            # Fallback z URL slug
            slug_match = re.search(r"/(?:prodej|pronajem)-[^/]+-([^-]+(?:-u-znojma)?)-id=", url)
            if slug_match:
                location = slug_match.group(1).replace("-", " ").title()

        # Zajistíme, aby location_text vždy obsahoval "Znojmo" – všechny C21 listingy
        # pocházejí ze Znojemského okresu (URL filter), ale LOKALITA vrací jen obec (např. "Dobšice").
        if location and "znojmo" not in location.lower() and "jihomoravsk" not in location.lower():
            location = f"{location}, okres Znojmo"
        elif not location:
            location = "okres Znojmo"

        # Popis
        description = self._extract_description(soup)

        # Fotky
        photos = self._extract_photos(soup)

        result = {
            "source_code": self.SOURCE_CODE,
            "external_id": external_id,
            "url": url,
            "title": title,
            "description": description,
            "price": price,
            "offer_type": offer_type,
            "property_type": property_type,
            "area": area,
            "location_text": location,
            "photos": photos,
        }

        # Přidej dispozici do titulku pokud tam chybí
        if disposition and disposition not in title:
            result["title"] = f"{title} ({disposition})"

        return result

    # ------------------------------------------------------------------
    # Pomocné metody
    # ------------------------------------------------------------------

    def _property_type_from_url(self, url: str) -> str:
        url_lower = url.lower()
        if "-domy-" in url_lower or "-dum-" in url_lower or "-rodinny-" in url_lower:
            return "House"
        if "-byty-" in url_lower or "-byt-" in url_lower:
            return "Apartment"
        if "-pozemky-" in url_lower or "-pozemek-" in url_lower:
            return "Land"
        if "-komercni-" in url_lower or "-kancelar" in url_lower:
            return "Commercial"
        if "-chata" in url_lower or "-chalupa" in url_lower:
            return "Cottage"
        return "Other"

    def _parse_detail_table(self, soup: BeautifulSoup) -> Dict[str, str]:
        """Parsuje parametrovou tabulku na detailu."""
        params: Dict[str, str] = {}
        for row in soup.select("table tr"):
            cells = row.select("td")
            if len(cells) >= 2:
                key = cells[0].get_text(strip=True).upper()
                val = cells[1].get_text(" ", strip=True)
                if key and val:
                    params[key] = val
        return params

    def _extract_price(self, soup: BeautifulSoup) -> Optional[float]:
        """Extrahuje číselnou cenu v Kč."""
        # Hledá vzor "X XXX XXX Kč" na stránce
        for el in soup.find_all(string=re.compile(r"\d[\d\s]+Kč")):
            txt = el.strip()
            if len(txt) > 30:
                continue
            nums = re.sub(r"[^\d]", "", txt)
            if nums and int(nums) > 10000:
                return float(int(nums))
        return None

    def _extract_description(self, soup: BeautifulSoup) -> str:
        """Extrahuje popis nemovitosti.

        Century21 používá Tailwind CSS – popis je v <div class="...whitespace-break-spaces...">
        ne v <p> tagech. Fallbacky: meta description, kratší paragrafy.
        """
        # Primární: div s Tailwind třídou whitespace-break-spaces (hlavní popis)
        for d in soup.select("div[class]"):
            cls = " ".join(d.get("class", []))
            if "whitespace-break-spaces" in cls or "whitespace-break" in cls:
                txt = d.get_text(" ", strip=True)
                if len(txt) > 50:
                    return txt[:5000]

        # Sekundární: hledat <p> s textem (snížený práh na 50 znaků)
        paragraphs = []
        for p in soup.select("p"):
            txt = p.get_text(" ", strip=True)
            if len(txt) > 50 and "cookie" not in txt.lower() and "souhlas" not in txt.lower() and "práva" not in txt.lower():
                paragraphs.append(txt)
        if paragraphs:
            return "\n\n".join(paragraphs[:8])

        # Fallback: og:description / meta description
        meta = soup.find("meta", attrs={"property": "og:description"}) or \
               soup.find("meta", attrs={"name": "description"})
        if meta:
            content = meta.get("content", "")
            if len(content) > 20:
                return content

        return ""

    def _extract_photos(self, soup: BeautifulSoup) -> List[str]:
        """Extrahuje URL fotek ze CDN igluu."""
        photos: List[str] = []
        seen: set = set()
        # Fotky jsou na igluu CDN: live-file-api.igluu.cz
        for img in soup.select("img[src*='igluu.cz']"):
            src = img.get("src", "").strip()
            if not src or src in seen:
                continue
            # Přeskočit náhledy / thumbnails (cesta neobsahuje UUID formát)
            if not re.search(r"file/[0-9a-f\-]{36}", src, re.I):
                continue
            seen.add(src)
            photos.append(src)
            if len(photos) >= 20:
                break

        # Fallback: hledej v href atributech (galerie)
        if not photos:
            for a in soup.select("a[href*='igluu.cz']"):
                href = a.get("href", "").strip()
                if href and href not in seen and re.search(r"file/[0-9a-f\-]{36}", href, re.I):
                    seen.add(href)
                    photos.append(href)
                    if len(photos) >= 20:
                        break

        return photos
