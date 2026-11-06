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

Zamčené nabídky (od poloviny října 2026): čerstvou nabídku Realingo zhruba den až dva ukazuje jen
předplatitelům – výpis i detail mají `isLocked: true` a chybí cena, plochy, fotky, popis i poloha.
Do 7. 11. 2026 se takový detail uložil jako prázdná kostra („Prodej rodinného domu", bez obce,
lokalita „okres Znojmo" z náhradní hodnoty – takže prošla i filtrem lokality). Po odemčení se kostra
opravila jen tehdy, když detail prošel filtrem a nepocházel z portálu, který bereme přímo; jinak
zůstala aktivní navždy (6. 11. 2026: 358 z 1 129 aktivních inzerátů, 235 z nich převzatých ze
Sreality/iDNES/Bazoše, zbytek mimo cenový limit nebo z obcí okresu Břeclav, které nebereme).
"""
import asyncio
import json
import logging
import re
import time
import zlib
from datetime import datetime, timezone
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


class LockedOffer(Exception):
    """Nabídka je zamčená (`isLocked`) – Realingo ji zatím ukazuje jen předplatitelům, bez údajů."""


class GoneOffer(Exception):
    """Detail hlásí prodáno / pronajato / smazáno."""


class RealingoScraper:
    """Scraper pro realingo.cz (Next.js, data v __NEXT_DATA__)."""

    SOURCE_CODE = "REALINGO"

    # Limit úlohy v runneru je 45 min; běh proto končí sám, jakmile vyčerpá rozpočet (viz iDNES).
    TIME_BUDGET_SECONDS = 36 * 60

    # Známé inzeráty dostanou čerstvý detail aspoň jednou za tolik dní (fotky, popis, cena), i když
    # se ve výpisu nic nezměnilo. Detail trvá ~0,5 s (6. 11. 2026: 866 detailů za 7 min), sedmina
    # z ~1 100 známých = ~160 detailů, necelé 2 minuty za noc navíc.
    ROTATION_DAYS = 7
    DETAIL_DELAY_SECONDS = 0.4

    def __init__(self, lists: Optional[List[Tuple[str, str, str]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        self.skipped_origin = 0
        self.locked = 0
        self.retired = 0
        # False = některou stránku výpisu se nepodařilo načíst; co jsme neviděli, nemusí být stažené
        self.lists_complete = True
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        # 40 položek na stránku; pozemky v Brně-venkově mají 781 položek = 20 stránek těsně
        max_pages = 30 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        """
        Dvě fáze: napřed všechny výpisy (levné), potom detaily – u nových inzerátů, při změně ceny,
        při změně galerie (výpis nese celou galerii, takže přenahrané fotky poznáme hned) a u
        sedminy známých denně (rotace). Původ inzerátu (externalUrl) je až v detailu a dvě třetiny
        nabídek pocházejí z portálů, které bereme přímo; ty si pamatujeme (scrape_skips), jinak by
        se jejich detail stahoval každou noc znovu jen proto, aby se zahodil.

        Známý inzerát, jehož čerstvý detail se neuloží (původ odjinud, filtr ho vyřadí, prodáno),
        se deaktivuje – jinak by ho „viděno ve výpisu" drželo aktivní se starými daty navždy.
        Proto se známé inzeráty vybrané k detailu neoznačují jako viděné předem, ale až podle
        výsledku; co se nestihne nebo selže, označí se na konci.
        """
        logger.info("Starting Realingo scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        started = time.monotonic()
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                db: Any = None
                pending_touch: set[str] = set()
                try:
                    db = get_db_manager()
                    known = await db.get_known_prices(self.SOURCE_CODE)
                    skipped = await db.get_skipped_ids(self.SOURCE_CODE)
                    galleries = await self._stored_galleries(db)

                    items: List[Dict[str, Any]] = []
                    seen: set[str] = set()
                    for path, property_type, offer_type in self.lists:
                        for item in await self._collect_list(path, property_type, offer_type, max_pages, metrics):
                            if item["id"] not in seen:
                                seen.add(item["id"])
                                items.append(item)

                    todo = self.select_for_detail(items, known, skipped, galleries, day=self.today())
                    pending_touch = {i["id"] for i in todo if i["id"] in known}
                    touched = await db.touch_listings(self.SOURCE_CODE, [
                        (i["id"], None, None) for i in items
                        if i["id"] in known and i["id"] not in skipped and i["id"] not in pending_touch])
                    # Uložené dřív, než se zjistilo, že pocházejí z portálu, který bereme přímo
                    for external_id in [i["id"] for i in items if i["id"] in known and i["id"] in skipped]:
                        if await db.deactivate_listing(self.SOURCE_CODE, external_id):
                            self.retired += 1
                    if not self.lists_complete:
                        kept = await db.mark_active_seen(self.SOURCE_CODE)
                        logger.warning("Realingo lists incomplete – %s active listings kept as seen", kept)

                    logger.info(
                        "Realingo lists: %s listings (%s locked), %s known refreshed, %s remembered as scraped "
                        "elsewhere, %s need detail (%s known: price, gallery or weekly rotation)",
                        len(items), sum(1 for i in items if i.get("locked")), touched,
                        sum(1 for i in items if i["id"] in skipped), len(todo), len(pending_touch))
                    self.scraped_count += touched

                    newly_skipped: List[str] = []
                    for idx, item in enumerate(todo):
                        if time.monotonic() - started >= self.TIME_BUDGET_SECONDS:
                            logger.info("Realingo time budget used up after %s details – %s left for the next run",
                                        idx, len(todo) - idx)
                            break
                        try:
                            outcome = await self._process_detail(db, item)
                        except Exception as exc:
                            logger.error("Error processing %s: %s", item.get("url"), exc)
                            metrics.increment_failed()
                            outcome = "error"

                        if outcome in ("saved", "filtered"):
                            self.scraped_count += 1
                            metrics.increment_scraped()
                        elif outcome == "elsewhere":
                            self.skipped_origin += 1
                            newly_skipped.append(item["id"])
                        elif outcome == "locked":
                            self.locked += 1
                        if outcome in ("saved", "filtered", "elsewhere", "gone"):
                            pending_touch.discard(item["id"])
                            if outcome != "saved" and item["id"] in known:
                                if await db.deactivate_listing(self.SOURCE_CODE, item["id"]):
                                    self.retired += 1
                        await asyncio.sleep(self.DETAIL_DELAY_SECONDS)

                    await db.remember_skipped(self.SOURCE_CODE, newly_skipped, "origin scraped directly")
                except Exception as exc:
                    logger.error("Realingo scraper failed: %r", exc)
                    metrics.increment_failed()
                finally:
                    # Vybrané k detailu, ale nezpracované (rozpočet, chyba, zamčené) – ve výpisu byly
                    if db is not None and pending_touch:
                        try:
                            self.scraped_count += await db.touch_listings(
                                self.SOURCE_CODE, [(external_id, None, None) for external_id in sorted(pending_touch)])
                        except Exception as exc:
                            logger.error("Realingo touch of unprocessed listings failed: %r", exc)
        self._http_client = None
        logger.info("Realingo scraper done. Scraped %s, skipped (origin already scraped) %s, locked %s, "
                    "deactivated %s (origin elsewhere, filtered out or sold)",
                    self.scraped_count, self.skipped_origin, self.locked, self.retired)
        return self.scraped_count

    async def _process_detail(self, db: Any, item: Dict[str, Any]) -> str:
        """Stáhne a uloží detail. Výsledek: saved / filtered (upsert ho vyřadil filtrem) /
        elsewhere (původ z portálu, který bereme přímo) / gone (prodáno, smazáno) / locked."""
        detail_html = await self._fetch(urljoin(BASE_URL, item["url"]))
        try:
            normalized = self.parse_detail_page(detail_html, item, item["property_type"], item["offer_type"])
        except LockedOffer:
            return "locked"
        except GoneOffer:
            return "gone"
        if normalized is None:
            return "elsewhere"
        return "saved" if await self._save_listing(normalized) else "filtered"

    async def _stored_galleries(self, db: Any) -> Optional[Dict[str, set]]:
        """
        external_id → URL uložených fotek. Výpis Realingo nese celou galerii, takže přenahranou
        galerii (makléř nahraje fotky znovu, všechny staré URL vrací 404) poznáme hned ze výpisu.
        None = nevíme (změnu galerie pak pozná až rotace).
        """
        try:
            async with db.acquire() as conn:
                rows = await conn.fetch(
                    """
                    SELECT l.external_id, array_agg(p.original_url) AS urls
                    FROM re_realestate.listings l
                    JOIN re_realestate.listing_photos p ON p.listing_id = l.id
                    WHERE l.source_code = $1
                    GROUP BY l.external_id
                    """,
                    self.SOURCE_CODE,
                )
        except Exception as exc:  # noqa: BLE001 – kontrola galerie je jen zrychlení, rotace ji pokryje
            logger.warning("Realingo stored galleries unavailable (%r) – gallery check skipped", exc)
            return None
        return {r["external_id"]: set(r["urls"] or []) for r in rows}

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
            district = self.district_from_path(path)
            for item in items:
                item["property_type"], item["offer_type"], item["district"] = property_type, offer_type, district
            collected.extend(items)
            if page * 40 >= (total or 0):
                break
            page += 1
            await asyncio.sleep(1.0)
        return collected

    @staticmethod
    def today() -> int:
        """Číslo dne (UTC) pro rotaci."""
        return datetime.now(timezone.utc).date().toordinal()

    @classmethod
    def in_rotation(cls, external_id: str, day: int, days: Optional[int] = None) -> bool:
        """
        Patří inzerát dnes do rotace? Stabilní hash external_id (vestavěný hash() se mezi procesy
        liší) rozdělí inzeráty do `days` skupin a každý den přijde na řadu jedna – každý známý
        inzerát tak dostane čerstvý detail jednou za `days` dní.
        """
        days = days or cls.ROTATION_DAYS
        return zlib.crc32(external_id.encode("utf-8")) % days == day % days

    @classmethod
    def select_for_detail(cls, items: List[Dict[str, Any]], known: Dict[str, Optional[float]],
                          skipped: set, galleries: Optional[Dict[str, set]] = None,
                          day: Optional[int] = None) -> List[Dict[str, Any]]:
        """
        Které položky potřebují detail, v pořadí: známé se změněnou cenou, nové, známé s fotkou,
        kterou ještě nemáme (přenahraná galerie), a známé z dnešní rotace (`day`; None = bez rotace).

        Zamčené nabídky ne – detail je stejně prázdný; přijdou na řadu po odemčení. Zapamatované
        „bereme odjinud" taky ne, ani když je kdysi uložila starší verze scraperu.
        """
        changed: List[Dict[str, Any]] = []
        new: List[Dict[str, Any]] = []
        gallery: List[Dict[str, Any]] = []
        rotation: List[Dict[str, Any]] = []
        for item in items:
            item_id = item["id"]
            if item.get("locked") or item_id in skipped:
                continue
            if item_id not in known:
                new.append(item)
                continue
            old_price, list_price = known[item_id], item.get("price")
            photos = set(item.get("photos") or [])
            if list_price is not None and (old_price is None or abs(float(old_price) - float(list_price)) > 0.5):
                changed.append(item)
            elif galleries is not None and photos and not photos <= galleries.get(item_id, set()):
                gallery.append(item)
            elif day is not None and cls.in_rotation(item_id, day):
                rotation.append(item)
        return changed + new + gallery + rotation

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

    @staticmethod
    def district_from_path(path: str) -> str:
        """„/prodej_domy/Okres_Brno-venkov/" → „Brno-venkov" (záloha, když detail okres neuvádí)."""
        match = re.search(r"/Okres_([^/]+)", path)
        return match.group(1).replace("_", " ") if match else ""

    @staticmethod
    def offer_price(offer: Dict[str, Any]) -> Optional[float]:
        """
        Celková cena z `price` (výpis i detail ho mají stejný): `canonical` (v Kč), jinak `total`.
        Typ PER_SQUARE_METER (pozemky „Kč/m²") nemá `total` – bez `canonical` je to Kč/m² × výměra.
        NEGOTIABLE („dohodou", „info v RK") = bez ceny. Do 7. 11. 2026 se brala jen `total`,
        takže pozemky s cenou za m² zůstaly bez ceny a obešly i cenový filtr.
        """
        price = offer.get("price") or {}
        for key in ("canonical", "total"):
            if price.get(key):
                return float(price[key])
        if price.get("type") == "PER_SQUARE_METER" and price.get("squareMeter"):
            area = offer.get("area") or {}
            size = area.get("plot") or area.get("main")
            if size:
                return float(round(float(price["squareMeter"]) * float(size)))
        return None

    @staticmethod
    def photo_urls(photos_raw: Optional[Dict[str, Any]]) -> List[str]:
        """Galerie (`photos.main` + `photos.list`, bez duplicit) → URL fotek; výpis i detail stejně."""
        photos_raw = photos_raw or {}
        paths: List[str] = []
        for p in [photos_raw.get("main")] + list(photos_raw.get("list") or []):
            if p and p not in paths:
                paths.append(p)
        return [f"{BASE_URL}/static/images/{p}.webp?w=1600" for p in paths][:50]

    def parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], int]:
        data = self._next_data(html)
        lst = data["props"]["pageProps"]["store"]["offer"]["list"]
        items = [
            {
                "id": str(o["id"]),
                "url": o["url"],
                "price": self.offer_price(o),
                "locked": bool(o.get("isLocked")),
                "photos": self.photo_urls(o.get("photos")),
            }
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
        offer = entry.get("offer") or {}
        if offer.get("isLocked") or entry.get("detail") is None:
            # Zamčená nabídka: bez ceny, ploch, fotek, popisu i polohy – uložila by se prázdná kostra
            raise LockedOffer(list_item["id"])
        if offer.get("soldOrRented") or offer.get("deleted") or entry.get("deleted"):
            raise GoneOffer(list_item["id"])
        detail = entry.get("detail") or {}
        location = entry.get("location") or {}

        origin = (detail.get("externalUrl") or "").lower()
        if any(dom in origin for dom in SKIP_ORIGIN_DOMAINS):
            logger.debug("Skip %s – origin %s is scraped directly", list_item["id"], origin)
            return None

        area = offer.get("area") or {}
        price = self.offer_price(offer)
        crumbs = location.get("breadcrumbs") or []
        okres = next((b["name"] for b in crumbs if str(b.get("name", "")).startswith("Okres")), "")
        district = okres.replace("Okres ", "").strip() or list_item.get("district") or ""
        obec = self._municipality(location.get("name") or "", crumbs, district)
        location_text = ", ".join(x for x in (obec, f"okres {district}" if district else "") if x)

        category = offer.get("category") or ""
        ptype = CATEGORY_MAP.get(category) or PROPERTY_MAP.get(offer.get("property") or "", property_type)
        otype = PURPOSE_MAP.get(offer.get("purpose") or "", offer_type)

        photo_urls = self.photo_urls(offer.get("photos"))

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
            "price": price,
            # Bez náhradního „okres Znojmo": s ním prošla filtrem lokality i nabídka z Hustopečí
            "location_text": location_text[:200],
            "municipality": obec[:100] or None,
            "district": district or None,
            "area_built_up": float(area["main"]) if area.get("main") and ptype != "Pozemek" else None,
            "area_land": float(area["plot"]) if area.get("plot") else (float(area["main"]) if ptype == "Pozemek" and area.get("main") else None),
            "rooms": rooms,
            "condition": STATUS_MAP.get(detail.get("buildingStatus") or ""),
            "construction_type": BUILDING_MAP.get(detail.get("buildingType") or ""),
            "photos": photo_urls,
        }
        # GPS je v offer.location (latitude/longitude + locationPrecision). „offer.gps" neexistuje –
        # do 7. 11. 2026 se GPS z Realinga nebrala vůbec. Přesnost TOWN je střed obce: ten doplní
        # geokódování s příznakem geocode_source a detekce duplicit ho nebere jako polohu domu.
        geo = offer.get("location") if isinstance(offer.get("location"), dict) else {}
        precision = str(geo.get("locationPrecision") or "").upper()
        if geo.get("latitude") and geo.get("longitude") and precision not in ("TOWN", "DISTRICT", "REGION", "COUNTRY"):
            result["latitude"], result["longitude"] = float(geo["latitude"]), float(geo["longitude"])
        if origin:
            result["description"] = (result["description"] + f"\n\nPůvodní inzerát: {detail['externalUrl']}")[:5000]
        if offer.get("reserved"):
            result["price_note"] = "Rezervace"
            result["keep_last_price"] = True
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> Optional[Any]:
        """Upsert; None = inzerát vyřadil filtr (settings.yaml)."""
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
        return listing_id
