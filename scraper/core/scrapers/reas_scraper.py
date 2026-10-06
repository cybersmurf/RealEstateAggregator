"""
Reas.cz scraper (www.reas.cz).

Site characteristics:
- Next.js SSR aplikace → kompletní data inzerátu jsou v __NEXT_DATA__ JSON
  přímo v HTML každé listingové stránky. Není potřeba Playwright ani JS rendering.
- GPS souřadnice dostupné přímo v SSR datech (point.coordinates = [lng, lat])
- Fotky: imagesWithMetadata[].original (Google Cloud Storage)
- external_id: MongoDB _id pole

Výpisy (od 6. 10. 2026):
- Jeden výpis na okres a typ: /prodej/{domy|byty|stavebni-pozemky}/okres-{slug}.
  Dřív se bral celý Jihomoravský kraj a okres se odhadoval – výpis okresu ho dává jistě.
- Stránkuje se parametrem `listPage` (ne `page` – ten web ignoruje a vrací pořád první
  stránku, což se dřív mylně přičítalo CDN cache). `sort=newest` drží pořadí stabilní.
- Neznámý segment nebo okres web tiše nahradí celostátním výpisem všech typů (count ~7000).
  Proto se u každé stránky kontroluje `adsListParams.locality.districtSlug`.
- `/_next/data/…json` se nepoužívá: s cestou výpisu vrací celostátní výpis a stránku ignoruje.
- Cenový strop v URL není – cenu řeší `search_filters` v settings.yaml.

Anonymizované inzeráty:
- Inzeráty s isAnonymized=true mají skrytou adresu, cenu a fotky (images:[]).
  Tyto inzeráty se záměrně nevyskytují ve veřejném vyhledávání a nelze je
  scrapeovat – REAS je zobrazuje pouze registrovaným uživatelům se subscripcí.
"""
import asyncio
import logging
import math
import re
import unicodedata
from typing import Any, Dict, List, Optional, Set, Tuple
import json as json_module

import httpx
from bs4 import BeautifulSoup

from ..http_utils import http_retry
from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager

logger = logging.getLogger(__name__)

BASE_URL = "https://www.reas.cz"

# (segment URL, slug okresu v URL, okres pro DB)
# Brno-město jen byty – domy a pozemky ve městě nejsou v cílovém rozsahu (viz settings.yaml).
# Segment pozemků je `stavebni-pozemky`; `pozemky` web nezná a vrátí celostátní výpis.
LISTS: List[Tuple[str, str, str]] = [
    ("domy", "znojmo", "Znojmo"),
    ("domy", "brno-venkov", "Brno-venkov"),
    ("byty", "znojmo", "Znojmo"),
    ("byty", "brno-venkov", "Brno-venkov"),
    ("byty", "brno-mesto", "Brno-město"),
    ("stavebni-pozemky", "znojmo", "Znojmo"),
    ("stavebni-pozemky", "brno-venkov", "Brno-venkov"),
]

# Mapování type z reas.cz → naše DB hodnoty
PROPERTY_TYPE_MAP: Dict[str, str] = {
    "flat": "Apartment",
    "house": "House",
    "land": "Land",
    "commercial": "Commercial",
    "cottage": "Cottage",
    "garage": "Garage",
    "other": "Other",
}

# subType je přesnější než type: dům i chata mají type "building"
SUBTYPE_PROPERTY_TYPES: Dict[str, str] = {
    "family_house": "House",
    "hut": "Cottage",
    "flat": "Apartment",
    "building_plot": "Land",
}

# Segment URL → typ nemovitosti (fallback, když `type`/`subType` inzerátu neznáme)
SEGMENT_PROPERTY_TYPES: Dict[str, str] = {
    "byty": "Apartment",
    "domy": "House",
    "pozemky": "Land",
    "stavebni-pozemky": "Land",
    "komerci": "Commercial",
}

# Segmenty URL k českému názvu pro title
SEGMENT_NAMES: Dict[str, str] = {
    "byty": "bytu",
    "domy": "domu",
    "pozemky": "pozemku",
    "stavebni-pozemky": "stavebního pozemku",
    "komerci": "komerční nemovitosti",
    "ostatni": "nemovitosti",
}

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9,en;q=0.8",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}

PAGE_LIMIT = 10  # reas.cz vrací 10 inzerátů na stránku
INCREMENTAL_PAGES = 2  # inkrementální běh: 20 nejnovějších z každého výpisu
# Pojistka pro případ, že stránka nenese adsListParams: výpis okresu má desítky inzerátů,
# celostátní náhradní výpis tisíce.
MAX_EXPECTED_LIST_COUNT = 500

_NEXT_DATA_RE = re.compile(
    r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>',
    re.DOTALL,
)


def _slugify(value: str) -> str:
    """"Hluboké Mašůvky" → "hluboke-masuvky" (stejný tvar jako municipalitySlug webu)."""
    decomposed = unicodedata.normalize("NFD", value.lower())
    plain = "".join(ch for ch in decomposed if unicodedata.category(ch) != "Mn")
    return re.sub(r"[^a-z0-9]+", "-", plain).strip("-")


class ReasScraper:
    """Scraper pro reas.cz – číst data z Next.js SSR __NEXT_DATA__."""

    SOURCE_CODE = "REAS"

    def __init__(
        self,
        fetch_details: bool = True,
        detail_concurrency: int = 5,
        lists: Optional[List[Tuple[str, str, str]]] = None,
    ):
        """
        Args:
            fetch_details: Fetchovat detail stránky pro popis. True = plná data.
            detail_concurrency: Počet paralelních detail požadavků.
            lists: Výpisy (segment, slug okresu, okres). Default LISTS.
        """
        self.fetch_details = fetch_details
        self.detail_concurrency = detail_concurrency
        self.lists = lists or LISTS
        # False = některý výpis se nepodařilo projít celý (chyba stránky, cizí výpis)
        self.lists_complete = True
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """Vstupní bod pro runner. Vrací počet inzerátů, které se podařilo zpracovat."""
        return await self.scrape(full_rescan=full_rescan)

    async def scrape(self, full_rescan: bool = False) -> int:
        logger.info("Starting Reas.cz scraper (full_rescan=%s, lists=%s)", full_rescan, len(self.lists))
        total = 0
        self.lists_complete = True
        seen_ids: Set[str] = set()

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                try:
                    for segment, district_slug, district in self.lists:
                        count = await self._scrape_list(
                            segment, district_slug, district, full_rescan, seen_ids, metrics,
                        )
                        total += count
                        logger.info("Reas.cz [%s/%s] done – %s listings", segment, district_slug, count)
                        await asyncio.sleep(1.0)

                    # Neúplný výpis: runner po plném běhu deaktivuje vše, co jsme „neviděli".
                    # Aktivní inzeráty proto necháme viděné a o stažení rozhodne až úplný běh.
                    if full_rescan and not self.lists_complete:
                        kept = await self._keep_active_seen()
                        logger.warning(
                            "Reas.cz lists incomplete – %s active listings kept as seen, nothing will be deactivated",
                            kept,
                        )
                finally:
                    self._http_client = None

        logger.info("Reas.cz scraper finished. Total scraped: %s (lists complete: %s)", total, self.lists_complete)
        return total

    async def _scrape_list(
        self,
        segment: str,
        district_slug: str,
        district: str,
        full_rescan: bool,
        seen_ids: Set[str],
        metrics: Any,
    ) -> int:
        """Projde stránky jednoho výpisu (typ × okres) a uloží inzeráty.

        Chyba stránky výpis ukončí a označí běh jako neúplný – co se stihlo uložit, zůstává
        a započítá se. Nikdy se nepokračuje „naslepo" dalším zdrojem dat.
        """
        label = f"{segment}/okres-{district_slug}"
        scraped = 0
        page = 1
        total_pages = 1

        while page <= total_pages:
            url = self.list_url(segment, district_slug, page)
            try:
                with timer(f"Fetch Reas list {label} page {page}"):
                    html = await self._fetch_html(url)
                count, ads_raw = self.parse_list_page(html, district_slug)
            except Exception as exc:  # noqa: BLE001 – síť, HTTP chyba i cizí výpis končí stejně
                logger.error("Reas.cz [%s] page %s failed, list stopped: %s", label, page, exc)
                metrics.increment_failed()
                self.lists_complete = False
                break

            if page == 1:
                pages_available = max(1, math.ceil(count / PAGE_LIMIT))
                total_pages = pages_available if full_rescan else min(pages_available, INCREMENTAL_PAGES)
                logger.info(
                    "Reas.cz [%s]: total=%s listings on %s pages, crawling %s",
                    label, count, pages_available, total_pages,
                )

            if not ads_raw:
                # Prázdná stránka před koncem výpisu = web vrátil méně, než sám ohlásil
                if count > 0:
                    logger.error("Reas.cz [%s] page %s of %s is empty, list stopped", label, page, total_pages)
                    self.lists_complete = False
                break

            # Anonymizované inzeráty (bez adresy, ceny a fotek) přeskakujeme; seen_ids hlídá
            # opakování, když se během procházení výpis posune o inzerát.
            ads: List[Dict[str, Any]] = []
            for ad in ads_raw:
                ad_id = ad.get("_id")
                if not ad_id or ad_id in seen_ids:
                    continue
                if ad.get("isAnonymized") or ad.get("isAnonymous"):
                    logger.debug("Reas.cz skipping anonymized listing %s", ad_id)
                    continue
                seen_ids.add(ad_id)
                ads.append(ad)

            sem = asyncio.Semaphore(self.detail_concurrency)
            results = await asyncio.gather(
                *(self._process_ad(ad, segment, district, sem, metrics) for ad in ads),
                return_exceptions=True,
            )
            for result in results:
                if isinstance(result, Exception):
                    logger.error("Reas.cz ad processing error: %s", result)
                elif result:
                    scraped += 1

            page += 1
            if page <= total_pages:
                await asyncio.sleep(1.0)

        return scraped

    async def _process_ad(
        self,
        ad: Dict[str, Any],
        segment: str,
        district: str,
        sem: asyncio.Semaphore,
        metrics: Any,
    ) -> bool:
        """Stáhne detail (popis, obec), sestaví listing a uloží ho."""
        async with sem:
            detail: Optional[Dict[str, Any]] = None
            detail_url = ad.get("link", "")
            if self.fetch_details and detail_url:
                try:
                    html = await self._fetch_html(detail_url)
                    detail = self._parse_detail(html)
                    await asyncio.sleep(0.3)
                except Exception as exc:  # noqa: BLE001
                    logger.warning("Reas.cz detail %s failed: %s", detail_url, exc)

            try:
                listing = self._build_listing(ad, segment, district, detail)
                if self.fetch_details and detail is None:
                    # Bez detailu není popis a upsert by jím přepsal ten uložený. Známý inzerát
                    # proto jen označíme jako viděný; nový se uloží v příštím běhu.
                    await self._touch_listing(listing)
                else:
                    await self._save_listing(listing)
                metrics.increment_scraped()
                return True
            except Exception as exc:  # noqa: BLE001
                logger.error("Reas.cz save %s error: %s", ad.get("_id"), exc)
                metrics.increment_failed()
                return False

    # ─── HTTP helpers ────────────────────────────────────────────────────────

    @staticmethod
    def list_url(segment: str, district_slug: str, page: int = 1) -> str:
        """URL stránky výpisu. Stránkuje `listPage`; parametr `page` web ignoruje."""
        url = f"{BASE_URL}/prodej/{segment}/okres-{district_slug}?sort=newest"
        if page > 1:
            url += f"&listPage={page}"
        return url

    @http_retry
    async def _fetch_html(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        resp = await self._http_client.get(url)
        resp.raise_for_status()
        return resp.text

    # ─── Parsování ───────────────────────────────────────────────────────────

    @staticmethod
    def _extract_page_props(html: str) -> Optional[Dict[str, Any]]:
        """pageProps ze __NEXT_DATA__ JSON (None, když skript chybí nebo není JSON)."""
        match = _NEXT_DATA_RE.search(html)
        if not match:
            return None
        try:
            page_props = json_module.loads(match.group(1))["props"]["pageProps"]
        except (KeyError, TypeError, json_module.JSONDecodeError) as exc:
            logger.debug("Reas.cz __NEXT_DATA__ parse error: %s", exc)
            return None
        return page_props if isinstance(page_props, dict) else None

    @classmethod
    def _extract_ads_list(cls, html: str) -> Optional[Dict[str, Any]]:
        """Extrahuj adsListResult ze __NEXT_DATA__ JSON."""
        page_props = cls._extract_page_props(html)
        if page_props is None:
            return None
        return page_props.get("adsListResult")

    @classmethod
    def parse_list_page(cls, html: str, district_slug: str) -> Tuple[int, List[Dict[str, Any]]]:
        """
        Stránka výpisu → (celkový počet inzerátů výpisu, inzeráty této stránky).

        Raises:
            ValueError: stránka nenese data výpisu, nebo web místo okresu vrátil jiný
                (celostátní) výpis – takové inzeráty se nesmí uložit pod naším okresem.
        """
        page_props = cls._extract_page_props(html)
        result = (page_props or {}).get("adsListResult")
        if not isinstance(result, dict):
            raise ValueError("adsListResult not found in __NEXT_DATA__")

        count = int(result.get("count") or 0)
        params = (page_props or {}).get("adsListParams")
        if isinstance(params, dict):
            got = (params.get("locality") or {}).get("districtSlug")
            if got != district_slug:
                raise ValueError(f"list is not for okres '{district_slug}' (districtSlug={got!r}, count={count})")
        elif count > MAX_EXPECTED_LIST_COUNT:
            raise ValueError(f"count={count} > {MAX_EXPECTED_LIST_COUNT} – locality filter does not seem to apply")

        ads = result.get("data") or []
        return count, [ad for ad in ads if isinstance(ad, dict)]

    @staticmethod
    def _parse_description(html: str) -> Optional[str]:
        """Extrahuj popis inzerátu z detail stránky."""
        # Zkus __NEXT_DATA__ nejprve
        match = _NEXT_DATA_RE.search(html)
        if match:
            try:
                nd = json_module.loads(match.group(1))
                detail = nd["props"]["pageProps"].get("adEstateDetail") or {}
                desc = detail.get("description") or detail.get("text")
                if desc:
                    return str(desc).strip()[:4000]
            except Exception:
                pass

        # Fallback: BeautifulSoup – hledáme popisný blok
        soup = BeautifulSoup(html, "html.parser")
        for selector in [
            "[class*='description']",
            "[class*='Description']",
            "[class*='about']",
            "article p",
        ]:
            el = soup.select_one(selector)
            if el:
                text = el.get_text(" ", strip=True)
                if len(text) > 50:
                    return text[:4000]
        return None

    @classmethod
    def _parse_detail(cls, html: str) -> Dict[str, Any]:
        """Detail inzerátu → popis a název obce (adEstateDetail.localityInfo má diakritiku)."""
        detail: Dict[str, Any] = {"description": cls._parse_description(html)}
        page_props = cls._extract_page_props(html) or {}
        estate = page_props.get("adEstateDetail")
        if isinstance(estate, dict):
            locality = estate.get("localityInfo")
            if isinstance(locality, dict) and locality.get("municipality"):
                detail["municipality"] = str(locality["municipality"]).strip()
        return detail

    @staticmethod
    def _municipality_from_ad(ad: Dict[str, Any]) -> Optional[str]:
        """
        Název obce z adresy výpisu. Adresa má tvary "Dyje 68, Dyje", "Vrbovec, okres Znojmo",
        "Nová Přímětická, Znojmo - Přímětice"; která část je obec, určí shoda s municipalitySlug.
        """
        slug = ad.get("municipalitySlug") or ""
        if not slug:
            return None
        for text in (ad.get("formattedLocation"), ad.get("formattedAddress")):
            for part in reversed((text or "").split(",")):
                part = part.strip()
                # "Znojmo - Přímětice" → obec je před pomlčkou; "Dyje 68" → bez čísla popisného
                for candidate in (part, part.split(" - ")[0], re.sub(r"\s+[\d/]+\w?$", "", part)):
                    candidate = candidate.strip()
                    if candidate and _slugify(candidate) == slug:
                        return candidate
        # Bez shody aspoň název ze slugu (bez diakritiky) – na párování obcí stačí
        return slug.replace("-", " ").title()

    # ─── Sestavení listingu ───────────────────────────────────────────────────

    def _build_listing(
        self,
        ad: Dict[str, Any],
        segment: str,
        district: str,
        detail: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        """Sestaví normalizovaný dict inzerátu z SSR dat výpisu (+ popis a obec z detailu).

        district: okres výpisu, ze kterého inzerát pochází ("Znojmo" / "Brno-venkov" /
            "Brno-město"). Adresa okres většinou nenese ("Hrušovanská, Hrabětice"), takže
            bez něj by inzerát neprošel geografickým filtrem v database.py.
        """
        detail = detail or {}
        external_id: str = ad["_id"]
        link: str = ad.get("link") or f"{BASE_URL}/inzerat/{external_id}"

        # Typ nemovitosti
        # `type` u domů je "building" (ne "house") – 63 domů kdysi skončilo jako „Ostatní"
        # a detekce duplicit je nepárovala. Proto nejdřív subType, pak type, nakonec segment URL.
        sub_type = str(ad.get("subType") or "").lower()
        reas_type = str(ad.get("type") or "").lower()
        property_type = (
            SUBTYPE_PROPERTY_TYPES.get(sub_type)
            or PROPERTY_TYPE_MAP.get(reas_type)
            or SEGMENT_PROPERTY_TYPES.get(segment, "Other")
        )

        # Plochy: u pozemku je jediná plocha jeho výměra, u domu užitná + pozemek
        utility_area = self._to_float(ad.get("utilityArea"))
        land_area = self._to_float(ad.get("landArea"))
        display_area = self._to_float(ad.get("displayArea"))
        if property_type == "Land":
            area_built_up: Optional[float] = None
            area_land = land_area or display_area
        else:
            area_built_up = utility_area or display_area
            area_land = land_area

        # Titulek – sestavíme z dostupných polí
        location_short = ad.get("formattedLocation") or ad.get("formattedAddress") or ""
        title_area = area_land if property_type == "Land" else area_built_up
        title_parts = ["Prodej", SEGMENT_NAMES.get(segment, "nemovitosti")]
        if title_area:
            # 117.0 → "117", 62.5 → "62.5" (formát :g by velké výměry zapsal exponentem)
            area_text = str(int(title_area)) if title_area == int(title_area) else str(title_area)
            title_parts.append(f"{area_text} m²")
        if location_short:
            title_parts.append(f"– {location_short}")
        title = " ".join(title_parts)[:200]

        # Cena: jen `price` (aktuální). `originalPrice` je cena před slevou – jako záloha
        # by k inzerátu bez ceny dostala starou cenu.
        price = self._to_float(ad.get("price"))
        if price is not None and price <= 0:
            price = None

        municipality = detail.get("municipality") or self._municipality_from_ad(ad)
        location_text = (
            ad.get("formattedLocation")
            or ad.get("formattedAddress")
            or municipality
            or ""
        )

        # GPS souřadnice [lng, lat] → latitude, longitude
        latitude: Optional[float] = None
        longitude: Optional[float] = None
        coords = (ad.get("point") or {}).get("coordinates")
        if coords and len(coords) >= 2:
            longitude = self._to_float(coords[0])
            latitude = self._to_float(coords[1])
            if latitude is None or longitude is None:
                latitude = longitude = None

        # Fotky – max 50, preferred: original, fallback: preview
        photos: List[str] = []
        for img in sorted(
            ad.get("imagesWithMetadata") or [],
            key=lambda x: x.get("order", 999),
        )[:50]:
            url = img.get("original") or img.get("preview")
            if url:
                photos.append(url)

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": external_id,
            "url": link,
            "title": title,
            "offer_type": "Sale",
            "property_type": property_type,
            "price": price,
            "area_built_up": area_built_up,
            "area_land": area_land,
            "location_text": location_text,
            "municipality": municipality,
            "district": district,
            "latitude": latitude,
            "longitude": longitude,
            # upsert_listing popis ořezává – None by tam spadlo
            "description": detail.get("description") or "",
            "photos": photos,
            "is_active": True,
        }

    @staticmethod
    def _to_float(value: Any) -> Optional[float]:
        if value is None or value == "":
            return None
        try:
            return float(value)
        except (ValueError, TypeError):
            return None

    # ─── Uložení do DB ────────────────────────────────────────────────────────

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db_manager = get_db_manager()
        await db_manager.upsert_listing(listing)

    async def _touch_listing(self, listing: Dict[str, Any]) -> None:
        """Známý inzerát označí jako viděný, aniž by přepsal uložená data (popis)."""
        await get_db_manager().touch_listings(
            self.SOURCE_CODE,
            [(listing["external_id"], listing.get("municipality"), listing.get("district"))],
        )

    async def _keep_active_seen(self) -> int:
        """Všechny aktivní inzeráty zdroje označí jako viděné (běh s neúplným výpisem)."""
        return await get_db_manager().mark_active_seen(self.SOURCE_CODE)
