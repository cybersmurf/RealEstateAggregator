"""
Bezrealitky.cz scraper – inzeráty soukromých prodejců (nejsou na Sreality).

Strategie: veřejné GraphQL API `https://api.bezrealitky.cz/graphql/` (stejné
používá web – Next.js + Apollo). Dotaz `listAdverts(regionOsmIds, estateType,
offerType, limit, offset)` vrací rovnou vše, co potřebujeme: id, uri, cenu,
plochy, dispozici, stav, konstrukci, adresu, GPS, popis, fotky (RECORD_MAIN)
i `regionTree` (kraj → okres → obec), takže detail stránku normálně netaháme.
Když API nedá popis nebo fotky, dotáhneme je ze SSR detailu
`/nemovitosti-byty-domy/<id>-<slug>` (`__NEXT_DATA__` → `apolloCache`).

Okresy filtrujeme přes OSM relation ID (`regionByUri("okres-znojmo").osmId`):
Znojmo R441326, Brno-venkov R442084, Brno-město R442273. Stránkuje se
limit/offset (web používá 15, my 30), řazení TIMEORDER_DESC. `totalCount`
říká celkem; 30. 9. 2026 bylo v JMK 28 domů (Znojmo 3, Brno-venkov 8,
Brno-město 5), pozemků Znojmo 13 / Brno-venkov 53 / Brno-město 17.

robots.txt zakazuje jen /vyhledat*, /search*, /moje-bezrealitky/* – API ani
detaily nezakazuje.
"""
import asyncio
import json
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple

import httpx

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://www.bezrealitky.cz"
API_URL = "https://api.bezrealitky.cz/graphql/"
PAGE_SIZE = 30

# Okres → OSM relation ID (z `regionByUri(uri:"okres-…").osmId`, prefix "R")
DEFAULT_DISTRICTS: Dict[str, str] = {
    "Znojmo": "R441326",
    "Brno-venkov": "R442084",
    "Brno-město": "R442273",
    "Břeclav": "R442309",   # 7. 10. 2026 – jen obce z partial_districts, zbytek zahodí filtr
}

# Co scrapovat: (estateType v API, typ nemovitosti, typ nabídky)
DEFAULT_LISTS: List[Tuple[str, str, str]] = [
    ("DUM", "Dům", "Prodej"),
    ("BYT", "Byt", "Prodej"),
    ("POZEMEK", "Pozemek", "Prodej"),
]

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "application/json, text/html;q=0.9, */*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Origin": BASE_URL,
    "Referer": BASE_URL + "/",
}

LIST_QUERY = """
query ListAdverts($regionOsmIds: [ID!], $estateType: [EstateType!], $offerType: [OfferType!], $limit: Int, $offset: Int) {
  listAdverts(regionOsmIds: $regionOsmIds, estateType: $estateType, offerType: $offerType,
              limit: $limit, offset: $offset, order: TIMEORDER_DESC, currency: CZK) {
    totalCount
    list {
      id uri type estateType offerType disposition landType houseType condition construction reconstruction ownership
      address(locale: CS) city(locale: CS) cityDistrict(locale: CS) street zip
      surface surfaceLand price currency originalPrice isDiscounted charges
      gps { lat lng }
      imageAltText(locale: CS)
      title description
      mainImage { id url(filter: RECORD_MAIN) }
      publicImages { id order url(filter: RECORD_MAIN) }
      regionTree(locale: CS) { id name lvl uri osmId }
      active reserved
    }
  }
}
"""

ESTATE_MAP = {
    "DUM": "Dům", "BYT": "Byt", "POZEMEK": "Pozemek", "REKREACNI_OBJEKT": "Chata",
    "GARAZ": "Garáž", "KANCELAR": "Komerční", "NEBYTOVY_PROSTOR": "Komerční",
}
OFFER_MAP = {"PRODEJ": "Prodej", "PRONAJEM": "Pronájem"}
CONDITION_MAP = {
    "VERY_GOOD": "Velmi dobrý", "GOOD": "Dobrý", "BAD": "Špatný", "CONSTRUCTION": "Ve výstavbě",
    "PROJECT": "Projekt", "NEW": "Novostavba", "DEMOLITION": "K demolici",
    "BEFORE_RECONSTRUCTION": "Před rekonstrukcí", "AFTER_RECONSTRUCTION": "Po rekonstrukci",
    "AFTER_PARTIAL_RECONSTRUCTION": "Po částečné rekonstrukci", "IN_RECONSTRUCTION": "V rekonstrukci",
}
CONSTRUCTION_MAP = {
    "WOOD": "Dřevostavba", "BRICK": "Cihla", "STONE": "Kámen", "PREFAB": "Montovaná",
    "PANEL": "Panel", "SKELET": "Skelet", "MIXED": "Smíšená",
}
LAND_TYPE_MAP = {
    "STAVEBNI": "stavební", "KOMERCNI": "komerční", "POLE": "pole", "LOUKA": "louka",
    "LES": "les", "RYBNIK": "rybník", "ZAHRADA": "zahrada", "OSTATNI": "ostatní",
}
# Zdroj inzerátu podle `type` – vše mimo UNDEFINED/BZR_COMFORT je import (RK, developer, fond)
PRIVATE_TYPES = ("UNDEFINED", "BZR_COMFORT", "COMFORT", "MANUAL_BZR_MAJITEL_PRODEJ", "MANUAL_BZR_MAJITEL_PRONAJEM")


class BezrealitkyScraper:
    """Scraper pro bezrealitky.cz (GraphQL API + SSR detail jako záloha)."""

    SOURCE_CODE = "BEZREALITKY"

    def __init__(self, lists: Optional[List[Tuple[str, str, str]]] = None,
                 districts: Optional[Dict[str, str]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.districts = districts or DEFAULT_DISTRICTS
        self.scraped_count = 0
        self.detail_fallbacks = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 20 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting Bezrealitky scraper (max_pages=%s, lists=%s, districts=%s)",
                    max_pages, len(self.lists), list(self.districts))
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                for estate_type, property_type, offer_type in self.lists:
                    for district, osm_id in self.districts.items():
                        await self._scrape_list(estate_type, property_type, offer_type, district, osm_id, max_pages, metrics)
        self._http_client = None
        logger.info("Bezrealitky scraper done. Scraped %s (detail fallbacks %s)", self.scraped_count, self.detail_fallbacks)
        return self.scraped_count

    async def _scrape_list(self, estate_type: str, property_type: str, offer_type: str, district: str,
                           osm_id: str, max_pages: int, metrics: Any) -> None:
        page = 1
        while page <= max_pages:
            offset = (page - 1) * PAGE_SIZE
            variables = {"regionOsmIds": [osm_id], "estateType": [estate_type], "offerType": ["PRODEJ"],
                         "limit": PAGE_SIZE, "offset": offset}
            try:
                with timer(f"Fetch list {estate_type} {district} page {page}"):
                    start = time.perf_counter()
                    data = await self._query(LIST_QUERY, variables)
                    metrics.record_fetch(time.perf_counter() - start)
                adverts, total = self.parse_list_response(data)
                if not adverts:
                    logger.info("No adverts for %s/%s page %s, stopping", estate_type, district, page)
                    break
                logger.info("%s/%s page %s: %s adverts (total %s)", estate_type, district, page, len(adverts), total)
                for advert in adverts:
                    try:
                        normalized = self.normalize_advert(advert, property_type, offer_type, district)
                        if not normalized.get("description") or not normalized.get("photos"):
                            await self._enrich_from_detail(normalized)
                            await asyncio.sleep(0.4)
                        await self._save_listing(normalized)
                        self.scraped_count += 1
                        metrics.increment_scraped()
                    except Exception as exc:
                        logger.error("Error processing advert %s: %s", advert.get("id"), exc)
                        metrics.increment_failed()
                if offset + len(adverts) >= total:
                    break
                page += 1
                await asyncio.sleep(1.0)
            except httpx.HTTPStatusError as exc:
                logger.error("HTTP error %s/%s page %s: %s", estate_type, district, page, exc)
                break
            except Exception as exc:
                logger.error("Error %s/%s page %s: %s", estate_type, district, page, exc)
                metrics.increment_failed()
                break

    async def _enrich_from_detail(self, listing: Dict[str, Any]) -> None:
        """Záloha: popis/fotky ze SSR detailu, když je API nevrátilo."""
        try:
            html = await self._fetch(listing["url"])
        except Exception as exc:
            logger.warning("Detail fallback failed for %s: %s", listing["url"], exc)
            return
        extra = self.parse_detail_page(html, listing["external_id"])
        self.detail_fallbacks += 1
        if not listing.get("description") and extra.get("description"):
            listing["description"] = extra["description"][:5000]
        if not listing.get("photos") and extra.get("photos"):
            listing["photos"] = extra["photos"][:50]
        if listing.get("latitude") is None and extra.get("latitude") is not None:
            listing["latitude"], listing["longitude"] = extra["latitude"], extra["longitude"]

    @http_retry
    async def _query(self, query: str, variables: Dict[str, Any]) -> Dict[str, Any]:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.post(API_URL, json={"query": query, "variables": variables},
                                                headers={"Content-Type": "application/json"})
        response.raise_for_status()
        data = response.json()
        if data.get("errors"):
            raise ValueError(f"GraphQL errors: {json.dumps(data['errors'], ensure_ascii=False)[:500]}")
        return data

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url, headers={"Accept": "text/html,application/xhtml+xml"})
        response.raise_for_status()
        return response.text

    # ── parsování (čisté funkce, testovatelné na uložených odpovědích) ────────

    @staticmethod
    def parse_list_response(data: Dict[str, Any]) -> Tuple[List[Dict[str, Any]], int]:
        """Z odpovědi `listAdverts` vrátí (seznam advertů, totalCount)."""
        lst = ((data.get("data") or {}).get("listAdverts")) or {}
        adverts = [a for a in (lst.get("list") or []) if a and a.get("id")]
        return adverts, int(lst.get("totalCount") or 0)

    @staticmethod
    def _disposition(code: Optional[str]) -> Tuple[Optional[str], Optional[int]]:
        if not code or code in ("UNDEFINED", "OSTATNI"):
            return None, None
        if code == "GARSONIERA":
            return "Garsoniéra", 1
        m = re.match(r"DISP_(\d)_(KK|1|IZB)$", code)
        if not m:
            return None, None
        rooms = int(m.group(1))
        suffix = {"KK": "kk", "1": "1", "IZB": "izb"}[m.group(2)]
        return f"{rooms}+{suffix}", rooms

    @staticmethod
    def _district_from_tree(region_tree: Optional[List[Dict[str, Any]]]) -> Optional[str]:
        for region in region_tree or []:
            name = str(region.get("name") or "")
            if region.get("lvl") == 3 or name.lower().startswith("okres "):
                return re.sub(r"^okres\s+", "", name, flags=re.I).strip() or None
        return None

    def normalize_advert(self, advert: Dict[str, Any], property_type: str, offer_type: str,
                         fallback_district: Optional[str] = None) -> Dict[str, Any]:
        """Advert z API → dict pro `upsert_listing`."""
        advert_id = str(advert["id"])
        uri = advert.get("uri") or advert_id
        ptype = ESTATE_MAP.get(advert.get("estateType") or "", property_type)
        otype = OFFER_MAP.get(advert.get("offerType") or "", offer_type)

        city = (advert.get("city") or "").strip()
        city_district = (advert.get("cityDistrict") or "").strip()
        district = self._district_from_tree(advert.get("regionTree")) or fallback_district
        place = city if not city_district or city_district == city or city_district.startswith(city) else f"{city} - {city_district}"
        location_text = ", ".join(x for x in (place, f"okres {district}" if district else "") if x)

        disposition, rooms = self._disposition(advert.get("disposition"))
        surface = advert.get("surface") or 0
        surface_land = advert.get("surfaceLand") or 0
        area_built = float(surface) if surface and ptype != "Pozemek" else None
        area_land = float(surface_land) if surface_land else (float(surface) if ptype == "Pozemek" and surface else None)

        title = (advert.get("title") or advert.get("imageAltText") or f"{otype} {ptype.lower()}").strip()
        if city and city not in title:
            title = f"{title}, {city}"

        description = (advert.get("description") or "").strip()
        notes: List[str] = []
        land_type = LAND_TYPE_MAP.get(advert.get("landType") or "")
        if land_type and ptype == "Pozemek":
            notes.append(f"Typ pozemku: {land_type}")
        if advert.get("reserved"):
            notes.append("Rezervováno")
        adv_type = advert.get("type") or "UNDEFINED"
        if adv_type not in PRIVATE_TYPES:
            notes.append(f"Import na Bezrealitky: {adv_type}")
        if notes:
            description = (description + "\n\n" + "\n".join(notes)).strip()

        images = sorted((advert.get("publicImages") or []), key=lambda i: i.get("order") or 0)
        photos: List[str] = []
        main = (advert.get("mainImage") or {}).get("url")
        if main:
            photos.append(main)
        for img in images:
            url = img.get("url")
            if url and url not in photos:
                photos.append(url)

        price = advert.get("price")
        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": advert_id,
            "url": f"{BASE_URL}/nemovitosti-byty-domy/{uri}",
            "title": title[:200],
            "description": description[:5000],
            "property_type": ptype,
            "offer_type": otype,
            "price": float(price) if price else None,
            "location_text": location_text[:200] or (f"okres {district}" if district else "Jihomoravský kraj"),
            "municipality": city[:100] or None,
            "district": district,
            "area_built_up": area_built,
            "area_land": area_land,
            "rooms": rooms,
            "disposition": disposition,
            "condition": CONDITION_MAP.get(advert.get("condition") or ""),
            "construction_type": CONSTRUCTION_MAP.get(advert.get("construction") or ""),
            "photos": photos[:50],
        }
        gps = advert.get("gps") or {}
        if isinstance(gps, dict) and gps.get("lat") is not None and gps.get("lng") is not None:
            result["latitude"], result["longitude"] = float(gps["lat"]), float(gps["lng"])
        return result

    @staticmethod
    def _next_data(html: str) -> Dict[str, Any]:
        m = re.search(r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>', html, re.S)
        if not m:
            raise ValueError("__NEXT_DATA__ not found (změnil se web?)")
        return json.loads(m.group(1))

    def parse_detail_page(self, html: str, advert_id: str) -> Dict[str, Any]:
        """SSR detail → {description, photos, latitude, longitude} z `apolloCache`."""
        data = self._next_data(html)
        cache = ((data.get("props") or {}).get("pageProps") or {}).get("apolloCache") or {}
        advert = cache.get(f"Advert:{advert_id}")
        if advert is None:
            advert = next((v for k, v in cache.items() if k.startswith("Advert:")), None)
        if not isinstance(advert, dict):
            return {}

        description = advert.get("description") or ""
        for key, value in advert.items():
            if key.startswith("descriptionByLocale(") and value and not description:
                description = value

        photos: List[str] = []
        refs = [advert.get("mainImage")] + list(advert.get("publicImages") or [])
        for ref in refs:
            if not isinstance(ref, dict):
                continue
            image = cache.get(ref.get("__ref") or "", ref)
            url = next((v for k, v in image.items() if k.startswith("url(") and "RECORD_MAIN" in k and v), None) \
                or next((v for k, v in image.items() if k.startswith("url(") and v), None)
            if url and url not in photos:
                photos.append(url)

        result: Dict[str, Any] = {"description": description.strip(), "photos": photos[:50]}
        gps = advert.get("gps") or {}
        if isinstance(gps, dict) and gps.get("lat") is not None and gps.get("lng") is not None:
            result["latitude"], result["longitude"] = float(gps["lat"]), float(gps["lng"])
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
