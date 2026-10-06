"""
UlovDomov.cz scraper – pronájmy a prodeje, hodně soukromých pronajímatelů.

Strategie: sitemap + SSR detail, BEZ API. Web (Next.js) tahá výpisy z
`POST https://ud.api.ulovdomov.cz/v1/offer/find` (hledání podle bounding boxu),
jenže robots.txt API hostu říká `User-agent: * / Disallow: /`, takže API
nepoužíváme vůbec. Stránky výpisu na www (`/prodej/domu/znojmo?lokace=Znojmo`)
mají v `__NEXT_DATA__` jen `count` a `bounds`, žádné inzeráty.

Co robots.txt www povoluje (`Allow: /` + odkaz na sitemap):
1. `https://www.ulovdomov.cz/sitemap-offers.xml` – všechny aktivní nabídky celé ČR
   (6. 10. 2026: 7 717 URL = 4 379 prodej + 3 294 pronájem + 44 spolubydlení;
   SSR `count` na /prodej/nemovitosti resp. /pronajem/nemovitosti hlásil 4 394 a 3 308).
2. Detail `/inzerat/<slug>/<id>` – v `__NEXT_DATA__` (`props.pageProps.offer.data`)
   je vše: okres, obec, část obce, ulice, GPS, cena, parametry, popis, fotky.
   Na slugu nezáleží, rozhoduje id.

Okresy: slug v sitemapě začíná obcí (`pronajem-znojmo-kasarna-kasarna-2-kk`,
u prodeje s prázdným prefixem: `-znojmo-primetice-postovni-fiveplusrooms`), takže
kandidáty předvybíráme podle seznamu obcí okresů Znojmo (144), Brno-venkov (187)
a Brna. Stejnojmenné obce jinde v ČR (Lešná u Vsetína, Říčany u Prahy…) vyřadí až
detail – okres bereme vždy z `district.name` detailu, nikdy ho neodhadujeme.

Omezení, o kterých je dobré vědět:
- Sitemap je statický soubor z buildu webu (všechna `lastmod` = čas nasazení),
  nové inzeráty se v ní objeví až po dalším nasazení portálu.
- Typ nemovitosti ze slugu spolehlivě nepoznáme, kategorie (dům/byt/pozemek) se
  filtruje až po stažení detailu. Jeden inzerát = jeden požadavek.
- `publishedAt` v detailu není datum zveřejnění, ale „volné od" ve formátu
  MM.DD.RRRR (bývá i v budoucnosti) – jako `date_created_source` ho nepoužíváme.
- Stažený inzerát vrací HTTP 200 se `status` ≠ `ACTIVE` (např. `IMPORT_IGNORED`).
- Telefon a e-mail jsou sice v `__NEXT_DATA__`, ale web je ukáže až po kliknutí
  na „Kontaktovat" – neukládáme je. Jméno a firma inzerenta jsou na stránce vidět.
"""
import asyncio
import json
import logging
import re
import time
from typing import Any, Dict, FrozenSet, Iterable, List, Optional, Set, Tuple

import httpx

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry
from ..district_municipalities import DISTRICT_MUNICIPALITY_SLUGS

logger = logging.getLogger(__name__)

BASE_URL = "https://www.ulovdomov.cz"
SITEMAP_URL = f"{BASE_URL}/sitemap-offers.xml"
MAX_PHOTOS = 20
REQUEST_DELAY = 1.0          # vteřin mezi detaily (max ~1 požadavek/s)
INCREMENTAL_MAX_DETAILS = 60  # inkrementální běh: jen N nejnovějších (nejvyšší id)

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
}

# Delší názvy obcí mimo naše okresy, které začínají stejně jako některá naše obec
# (Česká × Česká Lípa). Jen úspora požadavků – bez seznamu by je vyřadil detail.
SLUG_FALSE_FRIENDS: Tuple[str, ...] = (
    "ceska-lipa", "ceska-trebova", "ceska-kamenice", "ceska-skalice", "ceska-ves",
    "lomnice-nad-popelkou", "lomnice-nad-luznici", "brezany-ii", "petrovice-u-karvine",
    "ricky-v-orlickych-horach", "tvarozna-lhota", "hradek-nad-nisou",
)

# Konce slugů, které určitě nechceme (garáže, mobilheimy) – ušetří detail
SLUG_SKIP_SUFFIXES: Tuple[str, ...] = ("-garage", "-garageparking", "-garaz", "-mobilehome")

# Co ukládat: (offerTypeId, propertyType id na portálu)
DEFAULT_CATEGORIES: FrozenSet[Tuple[str, str]] = frozenset({
    ("sale", "house"), ("sale", "flat"), ("sale", "land"),
    ("rent", "flat"), ("rent", "house"),
})

OFFER_MAP = {"sale": "Prodej", "rent": "Pronájem"}
PROPERTY_MAP = {"house": "Dům", "flat": "Byt", "land": "Pozemek"}
# Chata / chalupa je na portálu druh domu (`houseType`)
COTTAGE_HOUSE_TYPES = ("chalet", "cottage")
CONDITION_MAP = {
    "veryGood": "Velmi dobrý", "good": "Dobrý", "bad": "Špatný", "newBuild": "Novostavba",
    "project": "Projekt", "inConstruction": "Ve výstavbě", "preReconstruction": "Před rekonstrukcí",
    "postReconstruction": "Po rekonstrukci", "inReconstruction": "V rekonstrukci", "demolition": "K demolici",
}
CONSTRUCTION_MAP = {
    "brick": "Cihla", "panel": "Panel", "assembled": "Montovaná", "skeleton": "Skelet",
    "stone": "Kámen", "wood": "Dřevostavba", "mixed": "Smíšená", "modular": "Montovaná",
}
ROOM_COUNT_MAP = {"oneRoom": 1, "twoRooms": 2, "threeRooms": 3, "fourRooms": 4, "fivePlusRooms": 5}

_RE_SITEMAP_URL = re.compile(r"<loc>\s*(https://www\.ulovdomov\.cz/inzerat/([^/<\s]*)/(\d+))\s*</loc>")
_RE_SLUG_PREFIX = re.compile(r"^(prodej|pronajem|spolubydleni)?-?")
_RE_NEXT_DATA = re.compile(r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>', re.S)
_RE_AREA = re.compile(r"(\d[\d\s\u00a0.]*(?:,\d+)?)\s*m(?:2|²)", re.I)
_RE_DISPOSITION = re.compile(r"^(\d)\+(kk|\d)$", re.I)


class UlovDomovScraper:
    """Scraper pro ulovdomov.cz (sitemap nabídek + SSR detail, API je v robots.txt zakázané)."""

    SOURCE_CODE = "ULOVDOMOV"

    def __init__(self, districts: Optional[Iterable[str]] = None,
                 categories: Optional[Iterable[Tuple[str, str]]] = None) -> None:
        self.districts: Tuple[str, ...] = tuple(districts) if districts else tuple(DISTRICT_MUNICIPALITY_SLUGS)
        self.categories: FrozenSet[Tuple[str, str]] = frozenset(categories) if categories else DEFAULT_CATEGORIES
        slugs: Set[str] = set()
        for district in self.districts:
            slugs |= DISTRICT_MUNICIPALITY_SLUGS.get(district, frozenset())
        # Nejdelší napřed, ať „ujezd-u-brna" vyhraje nad „ujezd"
        self._municipality_slugs: List[str] = sorted(slugs, key=len, reverse=True)
        self.scraped_count = 0
        self.skipped: Dict[str, int] = {}
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_details = None if full_rescan else INCREMENTAL_MAX_DETAILS
        return await self.scrape(max_details=max_details)

    async def scrape(self, max_details: Optional[int] = INCREMENTAL_MAX_DETAILS) -> int:
        logger.info("Starting UlovDomov scraper (max_details=%s, districts=%s)", max_details, list(self.districts))
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                try:
                    with timer("Fetch UlovDomov sitemap"):
                        start = time.perf_counter()
                        xml = await self._fetch(SITEMAP_URL)
                        metrics.record_fetch(time.perf_counter() - start)
                    entries = self.parse_sitemap(xml)
                    candidates = self.select_candidates(entries)
                    logger.info("UlovDomov sitemap: %s offers, %s candidates in target municipalities",
                                len(entries), len(candidates))
                    if max_details is not None:
                        candidates = candidates[:max_details]
                    for entry in candidates:
                        await self._scrape_detail(entry, metrics)
                        await asyncio.sleep(REQUEST_DELAY)
                except Exception as exc:
                    logger.error("UlovDomov scraper failed: %s", exc)
                    metrics.increment_failed()
        self._http_client = None
        logger.info("UlovDomov scraper done. Scraped %s, skipped %s", self.scraped_count, self.skipped)
        return self.scraped_count

    async def _scrape_detail(self, entry: Dict[str, str], metrics: Any) -> None:
        try:
            start = time.perf_counter()
            html = await self._fetch(entry["url"])
            metrics.record_fetch(time.perf_counter() - start)
            offer = self.parse_detail_page(html)
            reason = self.skip_reason(offer)
            if reason:
                self.skipped[reason] = self.skipped.get(reason, 0) + 1
                logger.debug("Skipping %s: %s", entry["url"], reason)
                return
            await self._save_listing(self.normalize_offer(offer or {}))
            self.scraped_count += 1
            metrics.increment_scraped()
        except httpx.HTTPStatusError as exc:
            if exc.response.status_code in (404, 410):
                self.skipped["http_404"] = self.skipped.get("http_404", 0) + 1
                return
            logger.error("HTTP error for %s: %s", entry["url"], exc)
            metrics.increment_failed()
        except Exception as exc:
            logger.error("Error processing offer %s: %s", entry.get("external_id"), exc)
            metrics.increment_failed()

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── parsování (čisté funkce, testovatelné na uložených odpovědích) ────────

    @staticmethod
    def parse_sitemap(xml: str) -> List[Dict[str, str]]:
        """sitemap-offers.xml → [{url, slug, external_id}] (bez duplicit, v pořadí souboru)."""
        entries: List[Dict[str, str]] = []
        seen: Set[str] = set()
        for match in _RE_SITEMAP_URL.finditer(xml or ""):
            url, slug, offer_id = match.group(1), match.group(2), match.group(3)
            if offer_id in seen:
                continue
            seen.add(offer_id)
            entries.append({"url": url, "slug": slug, "external_id": offer_id})
        return entries

    def match_municipality(self, slug: str) -> Optional[str]:
        """Slug obce z našich okresů, kterou slug inzerátu začíná (jinak None)."""
        prefix = _RE_SLUG_PREFIX.match(slug or "")
        if prefix and prefix.group(1) == "spolubydleni":
            return None
        rest = (slug or "")[prefix.end():] if prefix else (slug or "")
        if not rest or rest.endswith(SLUG_SKIP_SUFFIXES):
            return None
        if any(rest == ff or rest.startswith(ff + "-") for ff in SLUG_FALSE_FRIENDS):
            return None
        for municipality in self._municipality_slugs:
            if rest == municipality or rest.startswith(municipality + "-"):
                return municipality
        return None

    def select_candidates(self, entries: List[Dict[str, str]]) -> List[Dict[str, str]]:
        """Záznamy sitemapy z obcí našich okresů, nejnovější (nejvyšší id) napřed."""
        candidates = [e for e in entries if self.match_municipality(e.get("slug", ""))]
        candidates.sort(key=lambda e: int(e["external_id"]), reverse=True)
        return candidates

    @staticmethod
    def parse_detail_page(html: str) -> Optional[Dict[str, Any]]:
        """SSR detail → `props.pageProps.offer.data` (None, když stránka nabídku nenese)."""
        match = _RE_NEXT_DATA.search(html or "")
        if not match:
            raise ValueError("__NEXT_DATA__ not found (změnil se web?)")
        data = json.loads(match.group(1))
        offer = ((data.get("props") or {}).get("pageProps") or {}).get("offer") or {}
        if not isinstance(offer, dict):
            return None
        offer_data = offer.get("data")
        if not isinstance(offer_data, dict) or not offer_data.get("id"):
            return None
        return offer_data

    @staticmethod
    def _option_id(parameters: Dict[str, Any], key: str) -> Optional[str]:
        options = (parameters.get(key) or {}).get("options") or []
        first = options[0] if options and isinstance(options[0], dict) else {}
        return first.get("id") or None

    @staticmethod
    def _option_title(parameters: Dict[str, Any], key: str) -> Optional[str]:
        options = (parameters.get(key) or {}).get("options") or []
        first = options[0] if options and isinstance(options[0], dict) else {}
        return first.get("title") or None

    @staticmethod
    def _area(parameters: Dict[str, Any], key: str) -> Optional[float]:
        """Parametr typu „133 m2" → 133.0; „0 m2" a nesmysly → None."""
        value = (parameters.get(key) or {}).get("value")
        if isinstance(value, (int, float)):
            return float(value) if value > 0 else None
        match = _RE_AREA.search(str(value or ""))
        if not match:
            return None
        # Tečka je oddělovač tisíců jen před trojicí číslic („1.106 m2"), jinak desetinná
        text = re.sub(r"[\s\u00a0]", "", match.group(1))
        text = re.sub(r"\.(?=\d{3}(?:\D|$))", "", text).replace(",", ".")
        try:
            number = float(text)
        except ValueError:
            return None
        return number if number > 0 else None

    @staticmethod
    def _name(node: Any) -> str:
        return str(node.get("name") or "").strip() if isinstance(node, dict) else ""

    def skip_reason(self, offer: Optional[Dict[str, Any]]) -> Optional[str]:
        """Proč inzerát neuložit (None = uložit): neaktivní, mimo okresy, jiná kategorie."""
        if not offer:
            return "no_offer"
        if offer.get("status") != "ACTIVE":
            return f"status_{offer.get('status') or 'unknown'}"
        district = self._name(offer.get("district"))
        if district not in self.districts:
            return "other_district"
        parameters = offer.get("parameters") or {}
        category = (offer.get("offerTypeId") or "", self._option_id(parameters, "propertyType") or "")
        if category not in self.categories:
            return "other_category"
        return None

    def normalize_offer(self, offer: Dict[str, Any]) -> Dict[str, Any]:
        """`offer.data` z detailu → dict pro `upsert_listing`."""
        offer_id = str(offer["id"])
        parameters = offer.get("parameters") or {}
        portal_type = self._option_id(parameters, "propertyType") or ""
        property_type = PROPERTY_MAP.get(portal_type, "Ostatní")
        if portal_type == "house" and self._option_id(parameters, "houseType") in COTTAGE_HOUSE_TYPES:
            property_type = "Chata"
        offer_type = OFFER_MAP.get(offer.get("offerTypeId") or "", "Prodej")

        village = self._name(offer.get("village"))
        part = self._name(offer.get("villagePart"))
        street = self._name(offer.get("street"))
        district = self._name(offer.get("district")) or None
        place = village if not part or part == village else f"{village} - {part}"
        # Ulice bez adresy portál vyplňuje názvem obce / části
        street_part = street if street and street not in (village, part) else ""
        location_text = ", ".join(x for x in (street_part, place, f"okres {district}" if district else "") if x)

        usable = self._area(parameters, "usableArea") or self._area(parameters, "floorArea")
        estate = self._area(parameters, "estateArea")
        if property_type == "Pozemek":
            area_built: Optional[float] = None
            area_land = estate or usable
        elif property_type == "Byt":
            area_built, area_land = usable, None
        else:
            area_built, area_land = usable, estate

        disposition: Optional[str] = None
        rooms: Optional[int] = None
        disposition_title = (self._option_title(parameters, "disposition") or "").strip()
        match = _RE_DISPOSITION.match(disposition_title)
        if match:
            disposition, rooms = f"{match.group(1)}+{match.group(2).lower()}", int(match.group(1))
        elif disposition_title:
            disposition = disposition_title[:50]
        if rooms is None:
            rooms = ROOM_COUNT_MAP.get(self._option_id(parameters, "roomCount") or "")

        price, unit_note = self._price(offer, area_land if property_type == "Pozemek" else area_built)
        # price_note se v seznamech ukazuje jako štítek u ceny („Rezervace") – patří tam jen krátká
        # jednotková cena; volný text portálu („+ provize RK, energie zvlášť…") jde do popisu.
        raw_note = re.sub(r"\s+", " ", str(offer.get("priceNote") or "")).strip()
        price_note = unit_note or None

        title = re.sub(r"\bm2\b", "m²", str(offer.get("title") or f"{offer_type} {property_type.lower()}")).strip()
        if village and village not in title:
            title = f"{title}, {place}"

        owner = offer.get("owner") if isinstance(offer.get("owner"), dict) else {}
        seller_name = " ".join(x for x in (str(owner.get("firstName") or "").strip(),
                                           str(owner.get("surname") or "").strip()) if x) or None
        seller_company = str(owner.get("type") or "").strip() or None

        description = str(offer.get("description") or "").strip()
        notes: List[str] = []
        if raw_note:
            notes.append(f"Poznámka k ceně: {raw_note}")
        for item in offer.get("priceParameters") or []:
            if isinstance(item, dict) and item.get("title") and item.get("value"):
                notes.append(f"{item['title']}: {item['value']}")
        if offer.get("isNoCommission"):
            notes.append("Bez provize")
        if notes:
            description = (description + "\n\n" + "\n".join(notes)).strip()

        photos: List[str] = []
        for photo in offer.get("photos") or []:
            path = photo.get("path") if isinstance(photo, dict) else None
            if isinstance(path, str) and path.startswith("http") and path not in photos:
                photos.append(path)

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": offer_id,
            "url": offer.get("absoluteUrl") or f"{BASE_URL}/inzerat/{offer.get('seo') or 'x'}/{offer_id}",
            "title": title[:200],
            "description": description[:5000],
            "property_type": property_type,
            "offer_type": offer_type,
            "price": price,
            "price_note": price_note,
            "location_text": location_text[:200] or "Jihomoravský kraj",
            "municipality": village[:100] or None,
            "district": district,
            "area_built_up": area_built,
            "area_land": area_land,
            "rooms": rooms,
            "disposition": disposition,
            "condition": CONDITION_MAP.get(self._option_id(parameters, "buildingCondition") or ""),
            "construction_type": CONSTRUCTION_MAP.get(self._option_id(parameters, "material") or ""),
            "seller_name": seller_name,
            "seller_company": seller_company,
            "photos": photos[:MAX_PHOTOS],
        }
        gps = offer.get("geoCoordinates") or {}
        if isinstance(gps, dict) and gps.get("lat") is not None and gps.get("lng") is not None:
            try:
                result["latitude"], result["longitude"] = float(gps["lat"]), float(gps["lng"])
            except (TypeError, ValueError):
                pass
        return result

    @staticmethod
    def _price(offer: Dict[str, Any], area: Optional[float]) -> Tuple[Optional[float], str]:
        """Cena v Kč (prodej celkem, pronájem za měsíc) + poznámka k jednotkové ceně.

        `rentalPrice` nese cenu i u prodeje. `priceUnit` = perRealEstate / perMonth /
        perSqM (pozemky za m² – přepočítáme plochou, bez plochy cenu neuvádíme).
        """
        raw = (offer.get("rentalPrice") or {}).get("value") if isinstance(offer.get("rentalPrice"), dict) else None
        try:
            value = float(raw) if raw is not None else 0.0
        except (TypeError, ValueError):
            value = 0.0
        if value <= 1:
            return None, ""
        if offer.get("priceUnit") == "perSqM":
            note = f"{value:,.0f} Kč/m²".replace(",", " ")
            return (float(round(value * area)) if area else None), note
        return value, ""

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
