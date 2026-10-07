"""
Realingo.cz scraper – celostátní agregátor exportů realitek (Next.js SSR).

Strategie: httpx, data z `<script id="__NEXT_DATA__">` (výpis i detail mají
kompletní JSON: cena, plochy, fotky, poloha, popis, externí URL původního
inzerátu). Žádné parsování HTML kromě vytažení toho skriptu.
Výpis: /prodej_domy/Okres_Znojmo/, další stránky /prodej_domy/Okres_Znojmo/N_strana/
(40 položek na stránku, `store.offer.list.total` říká celkem).

Proč: 30. 9. 2026 měl Realingo v okrese Znojmo 364 domů, z 12 vzorků nám 4
chyběly (dražší domy od realitek, které neplatí Sreality). Realingo přebírá
i iDNES/Sreality – inzeráty, jejichž `externalUrl` vede na zdroj, který už
scrapujeme sami, přeskakujeme (SKIP_ORIGIN_DOMAINS), ať neplodíme duplicity.
"""
import asyncio
import json
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin

import httpx

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://www.realingo.cz"

# Co scrapovat: (cesta výpisu, typ nemovitosti, typ nabídky). Brno-venkov přidán 6. 10. 2026 –
# portál tam má 434 domů, 240 bytů a 781 pozemků a nebrali jsme z nich nic.
DEFAULT_LISTS: List[Tuple[str, str, str]] = [
    ("/prodej_domy/Okres_Znojmo/", "Dům", "Prodej"),
    ("/prodej_byty/Okres_Znojmo/", "Byt", "Prodej"),
    ("/prodej_pozemky/Okres_Znojmo/", "Pozemek", "Prodej"),
    ("/prodej_domy/Okres_Brno-venkov/", "Dům", "Prodej"),
    ("/prodej_byty/Okres_Brno-venkov/", "Byt", "Prodej"),
    ("/prodej_pozemky/Okres_Brno-venkov/", "Pozemek", "Prodej"),
    # Břeclav (7. 10. 2026) – jen obce z partial_districts, zbytek zahodí filtr. Slug je
    # s diakritikou: „Okres_Breclav" web přesměruje na ulici Okružní v Břeclavi.
    ("/prodej_domy/Okres_Břeclav/", "Dům", "Prodej"),
    ("/prodej_byty/Okres_Břeclav/", "Byt", "Prodej"),
    ("/prodej_pozemky/Okres_Břeclav/", "Pozemek", "Prodej"),
]

# Původní zdroj inzerátu, který už máme vlastním scraperem → přeskočit
SKIP_ORIGIN_DOMAINS = (
    "sreality.cz", "reality.idnes.cz", "bazos.cz", "remax-czech.cz",
    "century21.cz", "mmreality.cz", "reas.cz", "prodejme.to", "lexamo.cz",
    "deluxreality.cz", "hvreality.cz", "premiareality.cz",
    "nemovitostiznojmo.cz", "znojmoreality.cz",
)

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": BASE_URL,
}

CATEGORY_MAP = {
    "HOUSE_FAMILY": "Dům", "HOUSE_VILLA": "Dům", "HOUSE_COTTAGE": "Chata",
    "HOUSE_CHALET": "Chata", "HOUSE_FARM": "Dům", "HOUSE_OTHER": "Ostatní",
    "HOUSE_APARTMENT_BUILDING": "Komerční",
    "FLAT": "Byt", "LAND": "Pozemek", "COMMERCIAL": "Komerční", "OTHER": "Ostatní",
}
PROPERTY_MAP = {"HOUSE": "Dům", "FLAT": "Byt", "LAND": "Pozemek", "COMMERCIAL": "Komerční"}
PURPOSE_MAP = {"SELL": "Prodej", "RENT": "Pronájem", "AUCTION": "Dražba"}
STATUS_MAP = {
    "NEW_BUILDING": "Novostavba", "VERY_GOOD": "Velmi dobrý", "GOOD": "Dobrý",
    "AFTER_RECONSTRUCTION": "Po rekonstrukci", "BEFORE_RECONSTRUCTION": "Před rekonstrukcí",
    "UNDER_CONSTRUCTION": "Ve výstavbě", "PROJECT": "Projekt", "BAD": "Špatný",
    "FOR_DEMOLITION": "K demolici",
}
BUILDING_MAP = {"BRICK": "Cihla", "PANEL": "Panel", "WOOD": "Dřevostavba", "STONE": "Kámen", "MIXED": "Smíšená"}


class RealingoScraper:
    """Scraper pro realingo.cz (Next.js, data v __NEXT_DATA__)."""

    SOURCE_CODE = "REALINGO"

    # Limit úlohy v runneru je 45 min; běh proto končí sám, jakmile vyčerpá rozpočet (viz iDNES).
    TIME_BUDGET_SECONDS = 36 * 60

    def __init__(self, lists: Optional[List[Tuple[str, str, str]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        self.skipped_origin = 0
        # False = některou stránku výpisu se nepodařilo načíst; co jsme neviděli, nemusí být stažené
        self.lists_complete = True
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        # 40 položek na stránku; pozemky v Brně-venkově mají 781 položek = 20 stránek těsně
        max_pages = 30 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        """
        Dvě fáze: napřed všechny výpisy (levné), potom detaily – jen u inzerátů, které ještě
        neznáme, a při změně ceny. Původ inzerátu (externalUrl) je až v detailu a dvě třetiny
        nabídek pocházejí z portálů, které bereme přímo; ty si pamatujeme (scrape_skips),
        jinak by se jejich detail stahoval každou noc znovu jen proto, aby se zahodil.
        """
        logger.info("Starting Realingo scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        started = time.monotonic()
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                try:
                    db = get_db_manager()
                    known = await db.get_known_prices(self.SOURCE_CODE)
                    skipped = await db.get_skipped_ids(self.SOURCE_CODE)

                    items: List[Dict[str, Any]] = []
                    seen: set[str] = set()
                    for path, property_type, offer_type in self.lists:
                        for item in await self._collect_list(path, property_type, offer_type, max_pages, metrics):
                            if item["id"] not in seen:
                                seen.add(item["id"])
                                items.append(item)

                    touched = await db.touch_listings(
                        self.SOURCE_CODE, [(i["id"], None, None) for i in items if i["id"] in known])
                    if not self.lists_complete:
                        kept = await db.mark_active_seen(self.SOURCE_CODE)
                        logger.warning("Realingo lists incomplete – %s active listings kept as seen", kept)

                    todo = self.select_for_detail(items, known, skipped)
                    logger.info("Realingo lists: %s listings, %s known refreshed, %s remembered as scraped elsewhere, %s need detail",
                                len(items), touched, sum(1 for i in items if i["id"] in skipped), len(todo))

                    newly_skipped: List[str] = []
                    for idx, item in enumerate(todo):
                        if time.monotonic() - started >= self.TIME_BUDGET_SECONDS:
                            logger.info("Realingo time budget used up after %s details – %s left for the next run",
                                        idx, len(todo) - idx)
                            break
                        try:
                            detail_html = await self._fetch(urljoin(BASE_URL, item["url"]))
                            normalized = self.parse_detail_page(
                                detail_html, item, item["property_type"], item["offer_type"])
                            if normalized is None:
                                self.skipped_origin += 1
                                newly_skipped.append(item["id"])
                                continue
                            await self._save_listing(normalized)
                            self.scraped_count += 1
                            metrics.increment_scraped()
                        except Exception as exc:
                            logger.error("Error processing %s: %s", item.get("url"), exc)
                            metrics.increment_failed()
                        await asyncio.sleep(0.4)

                    await db.remember_skipped(self.SOURCE_CODE, newly_skipped, "origin scraped directly")
                    self.scraped_count += touched
                except Exception as exc:
                    logger.error("Realingo scraper failed: %r", exc)
                    metrics.increment_failed()
        self._http_client = None
        logger.info("Realingo scraper done. Scraped %s, skipped (origin already scraped) %s",
                    self.scraped_count, self.skipped_origin)
        return self.scraped_count

    async def _collect_list(self, path: str, property_type: str, offer_type: str,
                            max_pages: int, metrics: Any) -> List[Dict[str, Any]]:
        """Položky jednoho výpisu (všechny stránky), každá s typem z konfigurace výpisu."""
        collected: List[Dict[str, Any]] = []
        page = 1
        while page <= max_pages:
            url = urljoin(BASE_URL, path if page == 1 else f"{path.rstrip('/')}/{page}_strana/")
            try:
                with timer(f"Fetch list {path} page {page}"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
                items, total = self.parse_list_page(html)
            except Exception as exc:
                # Nenačtená stránka = neúplný výpis; pokračujeme s tím, co máme
                logger.error("Realingo list %s page %s failed: %r", path, page, exc)
                metrics.increment_failed()
                self.lists_complete = False
                break
            if not items:
                logger.info("No items on %s page %s, stopping", path, page)
                break
            logger.info("%s page %s: %s listings (total %s)", path, page, len(items), total)
            for item in items:
                item["property_type"], item["offer_type"] = property_type, offer_type
            collected.extend(items)
            if page * 40 >= (total or 0):
                break
            page += 1
            await asyncio.sleep(1.0)
        return collected

    @staticmethod
    def select_for_detail(items: List[Dict[str, Any]], known: Dict[str, Optional[float]],
                          skipped: set) -> List[Dict[str, Any]]:
        """
        Které položky potřebují detail: napřed známé se změněnou cenou, potom nové. Známé se
        stejnou cenou ne; zapamatované „bereme odjinud" taky ne.
        """
        changed: List[Dict[str, Any]] = []
        new: List[Dict[str, Any]] = []
        for item in items:
            if item["id"] in known:
                old_price, list_price = known[item["id"]], item.get("price")
                if list_price is not None and (old_price is None or abs(float(old_price) - float(list_price)) > 0.5):
                    changed.append(item)
            elif item["id"] not in skipped:
                new.append(item)
        return changed + new

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── parsování (čisté funkce, testovatelné na uloženém HTML) ──────────────

    @staticmethod
    def _next_data(html: str) -> Dict[str, Any]:
        m = re.search(r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>', html, re.S)
        if not m:
            raise ValueError("__NEXT_DATA__ not found (změnil se web?)")
        return json.loads(m.group(1))

    @staticmethod
    def _municipality(name: str, crumbs: List[Dict[str, Any]], district: str) -> str:
        """`location.name` je „Obec, Znojmo" (obec + okresní město) nebo „Ulice, Město" (ulice + město,
        pak má breadcrumbs i město). Do `municipality` patří jen obec – jinak se v UI rozpadnou
        stránky obcí a detekce duplicit nespáruje „Šanov, Znojmo" se „Šanov" ze Sreality."""
        city = next((b["name"] for b in crumbs[1:] if b.get("name") and not str(b["name"]).startswith("Okres")), "")
        if city:
            return str(city).strip()
        parts = [p.strip() for p in name.split(",") if p.strip()]
        if len(parts) >= 2 and parts[-1].lower() == district.lower():
            return parts[0]
        return parts[-1] if parts else name.strip()

    def parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], int]:
        data = self._next_data(html)
        lst = data["props"]["pageProps"]["store"]["offer"]["list"]
        items = [
            {"id": str(o["id"]), "url": o["url"], "price": ((o.get("price") or {}).get("total") or None)}
            for o in lst.get("data", []) if o.get("url")
        ]
        return items, int(lst.get("total") or 0)

    def parse_detail_page(self, html: str, list_item: Dict[str, Any], property_type: str, offer_type: str) -> Optional[Dict[str, Any]]:
        data = self._next_data(html)
        details = data["props"]["pageProps"]["store"]["offer"]["details"]
        entry = details.get(list_item["id"]) or next(iter(details.values()))
        # store.offer.details[id] = {offer: {id, preview, offer: {...ceny, plochy, fotky}, detail: {...popis}, location}}
        if "detail" not in entry and isinstance(entry.get("offer"), dict) and "detail" in entry["offer"]:
            entry = entry["offer"]
        offer = entry["offer"]
        detail = entry.get("detail") or {}
        location = entry.get("location") or offer.get("location") or {}

        origin = (detail.get("externalUrl") or "").lower()
        if any(dom in origin for dom in SKIP_ORIGIN_DOMAINS):
            logger.debug("Skip %s – origin %s is scraped directly", list_item["id"], origin)
            return None

        area = offer.get("area") or {}
        price = (offer.get("price") or {}).get("total")
        crumbs = location.get("breadcrumbs") or []
        okres = next((b["name"] for b in crumbs if str(b.get("name", "")).startswith("Okres")), "")
        district = okres.replace("Okres ", "").strip()
        obec = self._municipality(location.get("name") or "", crumbs, district)
        location_text = ", ".join(x for x in (obec, f"okres {district}" if district else "") if x)

        category = offer.get("category") or ""
        ptype = CATEGORY_MAP.get(category) or PROPERTY_MAP.get(offer.get("property") or "", property_type)
        otype = PURPOSE_MAP.get(offer.get("purpose") or "", offer_type)

        photos_raw = offer.get("photos") or {}
        photo_paths = [photos_raw.get("main")] + list(photos_raw.get("list") or [])
        photos = []
        for p in photo_paths:
            if p and p not in photos:
                photos.append(p)
        photo_urls = [f"{BASE_URL}/static/images/{p}.webp?w=1600" for p in photos][:50]

        rooms = detail.get("roomCount")
        main_area = area.get("main")
        typ_label = {"Dům": "rodinného domu", "Byt": "bytu", "Pozemek": "pozemku", "Chata": "chaty", "Komerční": "komerčního objektu"}.get(ptype, "nemovitosti")
        title = f"{otype} {typ_label}" + (f" {main_area} m²" if main_area else "") + (f", {obec}" if obec else "")
        if ptype == "Byt" and rooms:
            title = f"{otype} bytu {rooms} pokoje" + (f" {main_area} m²" if main_area else "") + (f", {obec}" if obec else "")

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": str(offer.get("id") or list_item["id"]),
            "url": urljoin(BASE_URL, offer.get("url") or list_item["url"]),
            "title": title[:200],
            "description": (detail.get("description") or "")[:5000],
            "property_type": ptype,
            "offer_type": otype,
            "price": float(price) if price else None,
            "location_text": location_text[:200] or "okres Znojmo",
            "municipality": obec[:100] or None,
            "district": district or None,
            "area_built_up": float(area["main"]) if area.get("main") and ptype != "Pozemek" else None,
            "area_land": float(area["plot"]) if area.get("plot") else (float(area["main"]) if ptype == "Pozemek" and area.get("main") else None),
            "rooms": rooms,
            "condition": STATUS_MAP.get(detail.get("buildingStatus") or ""),
            "construction_type": BUILDING_MAP.get(detail.get("buildingType") or ""),
            "photos": photo_urls,
        }
        geo = (offer.get("gps") or offer.get("geo") or {})
        if isinstance(geo, dict) and geo.get("lat") and geo.get("lng"):
            result["latitude"], result["longitude"] = float(geo["lat"]), float(geo["lng"])
        if origin:
            result["description"] = (result["description"] + f"\n\nPůvodní inzerát: {detail['externalUrl']}")[:5000]
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
