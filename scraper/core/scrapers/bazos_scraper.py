"""
Bazos.cz Reality scraper.

Žádné API – čisté HTML scraping přes httpx + BeautifulSoup.
Oblast: Znojmo (hlokalita=66902), polomer 25 km, cena do 8 500 000 Kč.
Paginace: offset-based (/20/, /40/, /60/, ...), 20 inzerátů/stránku.

Foto URL vzor: https://www.bazos.cz/img/{N}/{last3}/{id}.jpg
  kde last3 = poslední 3 čísla ID (zero-padded), N = pořadové číslo fotky.
"""

import asyncio
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple

import httpx
from bs4 import BeautifulSoup

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://reality.bazos.cz"
PHOTO_BASE = "https://www.bazos.cz"

# Hledání: (kategorie v adrese, PSČ středu, okruh v km). Bez cenového stropu – ceny řeší
# search_filters v settings.yaml. Do 6. 10. 2026 běželo jediné hledání „Znojmo + 25 km do 8,5 mil.":
# chyběl celý okres Brno-venkov, okraje okresu Znojmo (Moravskokrumlovsko, Vranovsko) i dražší domy.
# Okolí Brna se bere po kategoriích – všechno najednou by byly tisíce brněnských bytů.
SEARCHES: List[Tuple[str, str, int]] = [
    ("", "66902", 35),
    ("prodam/dum/", "60200", 30),
    ("prodam/chata/", "60200", 30),
    ("prodam/pozemek/", "60200", 30),
    ("prodam/zahrada/", "60200", 30),
    ("prodam/byt/", "60200", 30),
    ("pronajmu/dum/", "60200", 30),
    ("pronajmu/byt/", "60200", 30),
    # Pálava a Novomlýnské nádrže (7. 10. 2026): střed Mikulov, 20 km. Z okresu Břeclav pustí
    # filtr jen obce z partial_districts.
    ("prodam/dum/", "69201", 20),
    ("prodam/chata/", "69201", 20),
    ("prodam/pozemek/", "69201", 20),
    ("prodam/zahrada/", "69201", 20),
]

# Okres, jak ho Bazoš píše ve výpisu („Brno venkov 691 23") → náš název. Co tu není (Brno, Vyškov,
# Třebíč…), se z výpisu nebere – okruh hledání zasahuje i do sousedních okresů.
LIST_DISTRICTS: Dict[str, str] = {
    "znojmo": "Znojmo",
    "brno venkov": "Brno-venkov",
    "brno-venkov": "Brno-venkov",
    "břeclav": "Břeclav",
}


def search_url(category: str, postcode: str, radius: int, page: int) -> str:
    """Adresa stránky výpisu (page od 1); další stránky mají v cestě posun po 20."""
    params = f"hledat=&hlokalita={postcode}&humkreis={radius}&cenaod=&cenado=&order="
    if page <= 1:
        return f"{BASE_URL}/{category}?hledat=&rubriky=reality&hlokalita={postcode}&humkreis={radius}&cenaod=&cenado=&Submit=Hledat"
    return f"{BASE_URL}/{category}{(page - 1) * 20}/?{params}"


DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9,en;q=0.8",
    "Referer": BASE_URL,
}

# Kategorie z href breadcrumb: /prodam/{key}/ nebo /pronajem/{key}/
_CATEGORY_MAP: Dict[str, str] = {
    "dum": "Dům",
    "domy": "Dům",
    "byt": "Byt",
    "byty": "Byt",
    "pozemek": "Pozemek",
    "pozemky": "Pozemek",
    "chata": "Chata",
    "chaty": "Chata",
    "chalupa": "Chata",
    "chalupy": "Chata",
    "garaz": "Garáž",
    "garaze": "Garáž",
    "kancelar": "Komerční",
    "kancelary": "Komerční",
    "prostory": "Komerční",
    "sklad": "Komerční",
    "sklady": "Komerční",
    "restaurace": "Komerční",
    "hotely": "Komerční",
    "projekty": "Byt",  # nové projekty = byty v novostavbě
}

# Keywords pro detekci poptávkových inzerátů (ne nabídka → přeskočit)
_DEMAND_RE = re.compile(
    r"^\s*(?:hled[aá]m|hled[aá]me|koup[ií]m|koup[ií]me|popt[aá]v)",
    re.IGNORECASE,
)


# Slova, po kterých následující "X m²" patří MÍSTNOSTI, ne celé nemovitosti.
# Reálný případ: "kuchyní o výměře 10 m²" se uložilo jako plocha domu
# a cenový signál z toho spočítal 730 000 Kč/m².
_ROOM_CONTEXT_RE = re.compile(
    r"kuchyn|koupeln|lo[žz]nic|pokoj|j[íi]deln|ob[ýy]vac|chodb|p[řr]eds[íi]n|"
    r"sp[íi][žz]|komor|gar[áa][žz]|d[íi]ln|teras|balk[óo]n|lod[žz]i|sklep|"
    r"p[ůu]d[aěe]|verand|m[íi]stnost|wc|toalet|[šs]atn|z[áa]dve[řr]",
    re.IGNORECASE,
)

# Slova, po kterých "X m²" je výměra POZEMKU. Reálný případ (Práče): "pozemky o celkové
# výměře 820 m2" skončilo v zastavěné ploše a detekce duplikátů to vzala jako rozpor
# se SREALITY (180 m² dům / 820 m² pozemek).
_LAND_CONTEXT_RE = re.compile(r"pozem|zahrad|parcel", re.IGNORECASE)

# Katastrální území v titulku: "…, k.ú. Práče", "v k.ú. Lednice na Moravě", "k.ú. Kyjovice, okr. Znojmo"
_CADASTRE_RE = re.compile(
    r"[kK]\.\s?[úÚ]\.\s*"
    r"([A-ZÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ][^\W\d_]+"
    r"(?:\s+(?:(?:u|na|nad|pod|při|v|ve)\s+)?[A-ZÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ][^\W\d_]+)*)"
)

class BazosScraper:
    """Scraper pro reality.bazos.cz (okresy Znojmo a Brno-venkov, viz SEARCHES)."""

    SOURCE_CODE = "BAZOS"
    _MAX_PAGES_INCREMENTAL = 2    # ~40 nejnovějších inzerátů každého hledání
    _MAX_PAGES_FULL = 250         # pojistka; výpis končí sám (méně než 20 položek / 404)
    # Limit úlohy v runneru je 45 min; běh proto končí sám, jakmile vyčerpá rozpočet (viz iDNES).
    TIME_BUDGET_SECONDS = 36 * 60

    def __init__(self, searches: Optional[List[Tuple[str, str, int]]] = None) -> None:
        self.searches = searches or SEARCHES
        self.scraped_count = 0
        self.skipped_other_district = 0
        # False = některou stránku výpisu se nepodařilo načíst; co jsme neviděli, nemusí být stažené
        self.lists_complete = True
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = self._MAX_PAGES_FULL if full_rescan else self._MAX_PAGES_INCREMENTAL
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        """
        Dvě fáze: napřed výpisy všech hledání (okres a cena jsou už v položce výpisu), potom
        detaily – jen u inzerátů z našich okresů, které ještě neznáme nebo jim Bazoš změnil cenu.
        Známým se jen obnoví „naposledy viděno".
        """
        logger.info("Starting Bazos.cz scraper (max_pages=%s, searches=%s)", max_pages, len(self.searches))
        started = time.monotonic()

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                try:
                    db = get_db_manager()
                    known = await db.get_known_prices(self.SOURCE_CODE)

                    items: List[Dict[str, Any]] = []
                    seen_ids: set[str] = set()
                    for category, postcode, radius in self.searches:
                        for item in await self._collect_search(category, postcode, radius, max_pages, metrics):
                            if item["external_id"] not in seen_ids:
                                seen_ids.add(item["external_id"])
                                items.append(item)

                    touched = await db.touch_listings(
                        self.SOURCE_CODE,
                        [(i["external_id"], None, i["district"]) for i in items if i["external_id"] in known],
                    )
                    if not self.lists_complete:
                        kept = await db.mark_active_seen(self.SOURCE_CODE)
                        logger.warning("Bazos lists incomplete – %s active listings kept as seen", kept)

                    todo = self.select_for_detail(items, known)
                    logger.info(
                        "Bazos lists: %s listings in target districts, %s known refreshed, %s need detail, "
                        "%s skipped as other district",
                        len(items), touched, len(todo), self.skipped_other_district,
                    )

                    saved = 0
                    for idx, item in enumerate(todo):
                        if time.monotonic() - started >= self.TIME_BUDGET_SECONDS:
                            logger.info("Bazos time budget used up after %s details – %s left for the next run",
                                        idx, len(todo) - idx)
                            break
                        try:
                            with timer(f"Bazos detail {item['external_id']}", logging.DEBUG):
                                start = time.perf_counter()
                                detail_html = await self._fetch(item["detail_url"])
                                metrics.record_fetch(time.perf_counter() - start)

                            listing = self._parse_detail_page(detail_html, item)
                            if listing is None:
                                # Poptávkový inzerát (hledám…) – přeskočit
                                logger.debug("Bazos: skipping demand ad %s", item["external_id"])
                                metrics.increment_failed()
                                continue

                            if item.get("district"):
                                listing["district"] = item["district"]
                            await self._save_listing(listing)
                            saved += 1
                            metrics.increment_scraped()
                        except Exception as exc:
                            logger.error("Bazos: error processing detail %s: %s", item.get("detail_url"), exc)
                            metrics.increment_failed()
                        await asyncio.sleep(0.5)

                    self.scraped_count = touched + saved
                except Exception as exc:
                    logger.error("Bazos scraper failed: %r", exc)
                    metrics.increment_failed()

        self._http_client = None
        logger.info("Bazos.cz scraper finished. Scraped %s listings", self.scraped_count)
        return self.scraped_count

    async def _collect_search(self, category: str, postcode: str, radius: int,
                              max_pages: int, metrics: Any) -> List[Dict[str, Any]]:
        """Položky jednoho hledání ze všech stránek; jen inzeráty z našich okresů."""
        collected: List[Dict[str, Any]] = []
        for page in range(1, max_pages + 1):
            url = search_url(category, postcode, radius, page)
            try:
                with timer(f"Bazos list {category or 'vse'} page {page}", logging.DEBUG):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
            except httpx.HTTPStatusError as exc:
                if exc.response.status_code == 404:
                    break  # za poslední stránkou
                logger.error("Bazos: list page %s failed: %r", url, exc)
                self.lists_complete = False
                break
            except Exception as exc:
                logger.error("Bazos: list page %s failed: %r", url, exc)
                self.lists_complete = False
                break

            page_items, other_district, raw_count = self.parse_list_page(html)
            self.skipped_other_district += other_district
            collected.extend(page_items)
            if raw_count < 20:
                break
            await asyncio.sleep(1.0)  # zdvořilé crawlování

        logger.info("Bazos search %s%s+%skm: %s listings in target districts",
                    category, postcode, radius, len(collected))
        return collected

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── List page ──────────────────────────────────────────────────────────────

    _RE_LISTING_HREF = re.compile(r"/inzerat/(\d+)/")
    _RE_LIST_PRICE = re.compile(r"(\d[\d\s\xa0]{2,})\s*Kč")

    @classmethod
    def parse_list_page(cls, html: str) -> Tuple[List[Dict[str, Any]], int, int]:
        """
        Stránka výpisu → (položky z našich okresů, počet položek z jiných okresů, počet všech položek).

        Položka: external_id, detail_url, title, price (číslo nebo None – „Dohodou", „V textu"),
        district (náš název okresu). Okres je ve výpisu u každého inzerátu („Brno venkov 691 23").
        """
        soup = BeautifulSoup(html, "html.parser")
        items: List[Dict[str, Any]] = []
        other_district = 0
        raw_count = 0

        for node in soup.select("div.inzeraty"):
            link = node.select_one(".nadpis a[href]") or node.select_one(".inzeratynadpis a[href]")
            href = str(link.get("href", "")) if link else ""
            match = cls._RE_LISTING_HREF.search(href)
            if not match:
                continue
            raw_count += 1

            lok = node.select_one(".inzeratylok")
            lok_text = " ".join(lok.get_text(" ", strip=True).split()) if lok else ""
            district_text = re.sub(r"\d{3}\s?\d{2}\s*$", "", lok_text).strip().lower()
            district = LIST_DISTRICTS.get(district_text)
            if district is None:
                other_district += 1
                continue

            raw_path = href.split("?")[0]
            title_el = node.select_one(".nadpis a")
            price_el = node.select_one(".inzeratycena")
            price_match = cls._RE_LIST_PRICE.search(price_el.get_text(" ", strip=True)) if price_el else None
            items.append({
                "external_id": match.group(1),
                "detail_url": raw_path if raw_path.startswith("http") else BASE_URL + raw_path,
                "title": (title_el.get_text(" ", strip=True) if title_el else "")[:200],
                "price": float(re.sub(r"[\s\xa0]", "", price_match.group(1))) if price_match else None,
                "district": district,
            })

        return items, other_district, raw_count

    @staticmethod
    def select_for_detail(items: List[Dict[str, Any]], known: Dict[str, Optional[float]]) -> List[Dict[str, Any]]:
        """Detail potřebují známé inzeráty se změněnou cenou (napřed) a nové; ostatní ne."""
        changed: List[Dict[str, Any]] = []
        new: List[Dict[str, Any]] = []
        for item in items:
            if item["external_id"] not in known:
                new.append(item)
                continue
            old_price, list_price = known[item["external_id"]], item["price"]
            if list_price is not None and (old_price is None or abs(float(old_price) - list_price) > 0.5):
                changed.append(item)
        return changed + new

    # ── Detail page ────────────────────────────────────────────────────────────

    def _parse_detail_page(
        self, html: str, item: Dict[str, Any]
    ) -> Optional[Dict[str, Any]]:
        """
        Zparsuje detail stránku inzerátu.

        Returns:
            Dict s daty nebo None pokud jde o poptávkový inzerát (hledám...).
        """
        soup = BeautifulSoup(html, "html.parser")

        # Blok „Podobné inzeráty" pod detailem nese titulky, úryvky popisů (<div class=popis>),
        # ceny a lokality CIZÍCH inzerátů. Šatov 6. 10. 2026: dům 4+1 dostal popis sousední
        # novostavby 5+2kk a z něj plochu 180 m² – a nespároval se se Sreality.
        for similar in soup.select("div.podobne"):
            similar.decompose()

        # ── Titulek ──────────────────────────────────────────────────────────
        h1 = soup.find("h1")
        title_raw = h1.get_text(" ", strip=True) if h1 else item.get("title", "")
        title = " ".join(title_raw.split())[:200]  # normalizuj vícenásobné mezery

        # Přeskočit poptávkové inzeráty
        if _DEMAND_RE.search(title):
            return None

        # ── Typ nabídky a typ nemovitosti z breadcrumb odkazů ─────────────────
        offer_type, property_type = self._infer_types(soup, title, item["detail_url"])

        # ── Popis ────────────────────────────────────────────────────────────
        description = self._extract_description(soup)

        # ── Cena ─────────────────────────────────────────────────────────────
        price = self._extract_price(soup)

        # ── Lokalita ─────────────────────────────────────────────────────────
        location_text = self._extract_location(soup)

        # ── Plochy ───────────────────────────────────────────────────────────
        area_built_up, area_land = self._extract_areas(title, description, property_type)

        # ── Fotky ────────────────────────────────────────────────────────────
        photos = self._extract_photos(soup, item["external_id"])

        return {
            "source_code": self.SOURCE_CODE,
            "external_id": item["external_id"],
            "url": item["detail_url"],
            "title": title,
            "description": description,
            "property_type": property_type,
            "offer_type": offer_type,
            "price": price,
            "location_text": location_text,
            "municipality": self._extract_municipality(title),
            "area_built_up": area_built_up,
            "area_land": area_land,
            "photos": photos,
        }

    def _infer_types(
        self, soup: BeautifulSoup, title: str, detail_url: str
    ) -> Tuple[str, str]:
        """Určí offer_type (Prodej/Pronájem) a property_type z odkazů breadcrumbu."""
        offer_type = "Prodej"
        property_type = "Ostatní"

        # Projdi všechny <a> tagy – breadcrumb obsahuje kanonické URL vzory
        # např. https://reality.bazos.cz/prodam/dum/ nebo /pronajem/byt/
        for a in soup.find_all("a", href=True):
            href = str(a.get("href", "")).lower()

            # Detekce pronájmu
            if "/pronajem/" in href:
                offer_type = "Pronájem"

            # Detekce kategorie
            if property_type == "Ostatní":
                for key, pt in _CATEGORY_MAP.items():
                    if f"/{key}/" in href or href.endswith(f"/{key}"):
                        property_type = pt
                        break

            # Jakmile máme obojí, můžeme skončit (breadcrumb je na začátku stránky)
            if property_type != "Ostatní" and offer_type != "Prodej":
                break

        # Fallback: infer z titulku
        if property_type == "Ostatní":
            title_lower = title.lower()
            for key, pt in _CATEGORY_MAP.items():
                if key in title_lower:
                    property_type = pt
                    break

        # Fallback: infer ze slugu detailní URL
        if property_type == "Ostatní":
            slug = detail_url.lower()
            for key, pt in _CATEGORY_MAP.items():
                if f"-{key}-" in slug or slug.endswith(f"-{key}") or f"-{key}." in slug:
                    property_type = pt
                    break

        # Typ nabídky z titulku (pokud breadcrumb nepomohl)
        title_lower = title.lower()
        if "pronajem" in title_lower or "pronájem" in title_lower or "nájem" in title_lower:
            offer_type = "Pronájem"

        return offer_type, property_type

    # Řádky popisující management tlačítka Bazoše (viditelná jen majiteli inzerátu)
    _MGMT_LINE_RE = re.compile(
        r"^\s*(?:Smazat|Upravit|Topovat|Nahlásit|Oblíbené|P[řr]idat do|Spam|Tisk|Facebook|Sdílejte|Doporučit|Podobné|[-\[\d\.]+\s*\d{4}\]?)\s*/?\s*(?:Smazat|Upravit|Topovat)?\s*$",
        re.IGNORECASE,
    )

    def _extract_description(self, soup: BeautifulSoup) -> str:
        """Extrahuje popis nemovitosti."""
        # Bazos.cz má popis inzerátu v <div class=popisdetail>; <div class=popis> je úryvek
        # v seznamu a v bloku podobných inzerátů, proto až jako záloha
        for selector in (".popisdetail", ".popis", "#popis", ".inzerat-popis", ".detailpopis"):
            el = soup.select_one(selector)
            if el:
                return self._clean_description(el.get_text("\n", strip=True))

        # Fallback: div s class obsahujícím "popis" nebo "desc" (ale NE maincontent – příliš obecný)
        el = soup.find("div", class_=re.compile(r"popis|desc", re.I))
        if el:
            return self._clean_description(el.get_text("\n", strip=True))

        # Poslední fallback: maincontent – filtruj management řádky
        el = soup.select_one("div.maincontent")
        if el:
            return self._clean_description(el.get_text("\n", strip=True))

        return ""

    def _clean_description(self, raw: str) -> str:
        """Odstraní z popisu management tlačítka, datum zveřejnění a prázdné řádky."""
        lines = []
        for line in raw.splitlines():
            stripped = line.strip()
            if not stripped:
                continue
            # Přeskočit řádky s management tlačítky nebo datem publikace
            if self._MGMT_LINE_RE.match(stripped):
                continue
            # Přeskočit řádky které jsou jen datum ve formátu "- [D.M. YYYY]" nebo "DD.M. YYYY"
            if re.match(r'^[-\s]*\[?\d{1,2}\.\s*\d{1,2}\.\s*\d{4}\]?\s*$', stripped):
                continue
            lines.append(stripped)
        return "\n".join(lines)[:5000]

    def _extract_price(self, soup: BeautifulSoup) -> Optional[float]:
        """Extrahuje cenu ve formátu 'Cena: X Kč' nebo 'X Kč'."""
        body_text = soup.get_text(" ")

        # Primární: 'Cena: X Kč' – nejpřesnější
        m = re.search(r"Cena\s*:\s*([\d][\d\s\xa0]*)\s*Kč", body_text, re.IGNORECASE)
        if m:
            raw = re.sub(r"[\s\xa0]", "", m.group(1))
            try:
                return float(raw)
            except ValueError:
                pass

        # Sekundární: jen 'X Kč' s aspoň 4 číslicemi (aby se vyloučily malé částky)
        m = re.search(r"(\d{4}[\d\s\xa0]*)\s*Kč", body_text)
        if m:
            raw = re.sub(r"[\s\xa0]", "", m.group(1))
            try:
                val = float(raw)
                if val >= 1000:
                    return val
            except ValueError:
                pass

        return None

    # Slova, kterými inzerenti začínají titulek místo lokality
    _TITLE_NON_PLACE_RE = re.compile(
        r"^(prodej|prodám|prodám|pronájem|pronajmu|koupě|koupím|nabízím|sleva|exkluzivn|"
        r"rd\b|byt\b|dům|chata|chalupa|pozemek|zahrada|garáž|novostavba|stavební|"
        # "Zemědělská půda, prodej, Džbánice" / "Rodinný dům Jevišovice" / "NA SAMOTĚ" / "TinyHouse"
        r"zeměd|orn[áé]|rodinn|samot|na\s+samot|tiny|sady\b|sad\b|vinic|vinn|sklep|les\b|lesn|louk|"
        r"rekrea|luxus|investi|útuln|krásn|prostorn)",
        re.IGNORECASE,
    )

    # Titulky ze seznamu jsou useknuté na 60 znaků – "…, k.ú. Dobšice u Znojm", "…, k.ú. Vr"
    _TITLE_MAX_LEN = 60
    _CADASTRE_CONNECTORS = {"u", "na", "nad", "pod", "při", "v", "ve"}

    @classmethod
    def _extract_municipality(cls, title: str) -> Optional[str]:
        """Obec z titulku – bazošská konvence je "Lechovice, prodej RD 5+1, …",
        případně katastrální území "…, k.ú. Práče".

        Bazoš neposílá GPS ani strukturovanou lokalitu (jen "PSČ Okresní-město"),
        takže bez tohohle nemá detekce duplikátů ani geo filtr u Bazoše co porovnávat.
        """
        if not title:
            return None

        leading = cls._leading_place(title)
        if leading:
            return leading

        m = _CADASTRE_RE.search(title)
        if not m or len(m.group(1)) > 40:
            return None

        words = m.group(1).split()
        if m.end() == len(title) and len(title) == cls._TITLE_MAX_LEN:
            # Useknutý titulek: poslední slovo je nejspíš neúplné ("Znojm"), zahodit i visící "u"
            words = words[:-1]
            while words and words[-1] in cls._CADASTRE_CONNECTORS:
                words.pop()
        return " ".join(words) or None

    @classmethod
    def _leading_place(cls, title: str) -> Optional[str]:
        if "," not in title:
            return None

        first = title.split(",", 1)[0].strip()
        if not first or not first[0].isupper():
            return None
        if any(ch.isdigit() for ch in first):
            return None
        if len(first.split()) > 3 or len(first) > 40:
            return None
        if cls._TITLE_NON_PLACE_RE.match(first):
            return None
        return first

    def _extract_location(self, soup: BeautifulSoup) -> str:
        """Extrahuje lokalitu ve formátu 'PSČ Město'."""
        body_text = soup.get_text(" ")

        # 'Lokalita: [Mapa] PSČ Město'
        m = re.search(
            r"Lokalita\s*:?\s*(?:Mapa\s*)?((?:\d{3}\s?\d{2})\s+\w[\w\s\-,\.]+?)(?:\s+Vidělo|\s+Cena\s*:|\n|$)",
            body_text,
            re.IGNORECASE,
        )
        if m:
            return " ".join(m.group(1).split())[:100]  # normalizuj vícenásobné mezery

        # Fallback: PSČ vzor (5 číslic, příp. s mezerou) + název města
        m = re.search(r"(\d{3}\s?\d{2})\s+([A-ZÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ][a-záčďéěíňóřšťúůýž\s\-]+)", body_text)
        if m:
            return f"{m.group(1)} {m.group(2).strip()}"[:100]

        return ""

    def _extract_areas(
        self, title: str, description: str, property_type: str = "Ostatní"
    ) -> Tuple[Optional[float], Optional[float]]:
        """Extrahuje užitnou/obestavěnou plochu a plochu pozemku (m²)."""
        full_text = title + " " + description

        # Plocha pozemku: 'pozemek o výměře X m²' nebo 'zahrada X m²' apod.
        area_land: Optional[float] = None
        # pozem\w* / zahrad\w* pokrývá i 1. pád ("pozemek 800 m²") – původní
        # pozemk(?:u|em|y|ů|a) na "pozemek" vůbec nesedělo; (?:\w+\s+)? = "o celkové výměře"
        # Strukturovaný zápis z exportu RK: "plocha parcely (m2): 551" / "pozemek (m2): 551"
        m_land = re.search(
            r"(?:plocha\s+(?:parcely|pozemku)|pozem\w*|parcel\w*)\s*\(m[²2]\)\s*:?\s*(\d{2,6})",
            full_text,
            re.IGNORECASE,
        ) or re.search(
            # (?:je|činí|má) – "Celková plocha pozemku je 1.223m2"
            r"(?:pozem\w*|zahrad\w*|parcel\w*)\s+(?:(?:je|činí|má)\s+)?(?:o\s+(?:\w+\s+)?(?:výměře|velikosti|ploše)\s+)?([\d][\d\s\.]+)\s*(?:m[²2]|㎡)",
            full_text,
            re.IGNORECASE,
        )
        if m_land:
            raw = re.sub(r"[\s\.]", "", m_land.group(1)).replace(",", ".")
            try:
                val = float(raw)
                if 10 <= val <= 100000:
                    area_land = val
            except ValueError:
                pass

        # Zastavěná plocha: preferuj 'zastavěnou plochou X m²' / 'užitnou X m²' z popisu
        area_built_up: Optional[float] = None
        # ploch\w* pokrývá i 1. pád "užitná plocha" – dřívější (plochou?|plochem?)
        # matchovalo jen 7. pád, takže nejběžnější formulace propadla na fallback
        # Nejdřív "užitná plocha (m2): 170" (jednotka PŘED číslem – export RK na Bazoši),
        # užitná má přednost před zastavěnou; pak klasické "užitná plocha 170 m²".
        m_built = re.search(
            r"(?:u[žz]itn[aáíé]+|obytn[aáíé]+)\s+ploch\w*\s*\(m[²2]\)\s*:?\s*(\d{2,5})",
            full_text,
            re.IGNORECASE,
        ) or re.search(
            r"zastav[eě]n[oaáíé]+\s+ploch\w*\s*\(m[²2]\)\s*:?\s*(\d{2,5})",
            full_text,
            re.IGNORECASE,
        ) or re.search(
            r"(?:zastav[eě]n[oaáíé]+|u[žz]itn[aáíé]+|obytn[aáíé]+)\s+ploch\w*[^\d]*(?<![\d,.])(\d{2,5})(?:[,.]\d+)?\s*(?:m[²2]|㎡)",
            full_text,
            re.IGNORECASE,
        )
        if m_built:
            try:
                val = float(m_built.group(1))
                if 10 <= val <= 2000:
                    area_built_up = val
            except ValueError:
                pass

        # Fallback: 'X m²' v POPISU (ne titulku), aby titulek nefalšoval výsledek.
        # POZOR: nesmí sebrat výměru MÍSTNOSTI – reálný případ: "kuchyní o výměře 10 m²"
        # se uložilo jako plocha domu a cenový signál pak počítal 730 000 Kč/m².
        if area_built_up is None:
            # (?<![\d,.]) – "pokoj 12,45 m²" nesmí dát 45 (desetinná část)
            for m in re.finditer(r"(?<![\d,.])(\d{2,5})(?:[,.]\d+)?\s*(?:m[²2]|㎡)", description):
                context = description[max(0, m.start() - 60):m.start()]
                if _ROOM_CONTEXT_RE.search(context):
                    continue  # jde o pokoj/kuchyň/garáž, ne o celý dům
                try:
                    val = float(m.group(1))
                except ValueError:
                    continue
                # Jen úsek od konce předchozí věty / předchozí plochy – "zahrada 450 m². Dům má 120 m²"
                own_clause = re.split(r"[.;!?]\s|m[²2]|㎡", context)[-1]
                # "o celkové výměře 322 m² (dle výpisu z katastru nemovitostí)" – katastr eviduje pozemek
                # (zastavěná plocha a nádvoří), ne užitnou plochu domu. Znojmo centrum 1. 10. 2026:
                # dům 150 m² se uložil jako 322 m² a nespároval se Sreality.
                after = description[m.end():m.end() + 60]
                cadastral = bool(re.search(r"katastr|\bLV\b|list[ue]? vlastnictví", after, re.IGNORECASE))
                if property_type != "Pozemek" and (val == area_land or cadastral or _LAND_CONTEXT_RE.search(own_clause)):
                    if area_land is None and 10 <= val <= 100000:
                        area_land = val
                    continue  # výměra pozemku, ne plocha domu
                if not (10 <= val <= 5000):
                    continue
                if property_type == "Pozemek":
                    # U pozemku plocha patří vždy do area_land; pokud už ji máme,
                    # nesmí se stejné číslo propsat i do zastavěné plochy
                    if area_land is None:
                        area_land = val
                    break
                # Dům pod 40 m² je téměř jistě špatně přiřazená místnost
                if property_type == "Dům" and val < 40:
                    continue
                area_built_up = val
                break

        # Pokud pořád nic, zkus titulek – ale jen pro Pozemek kde to jde do area_land
        if area_built_up is None and area_land is None:
            m = re.search(r"(\d{2,5})\s*(?:m[²2]|㎡)", title)
            if m:
                try:
                    val = float(m.group(1))
                    if 10 <= val <= 100000:
                        # "…sklepem a zahradou, 1223 m²" – číslo hned za zahradou/pozemkem je pozemek
                        after_land_word = _LAND_CONTEXT_RE.search(title[max(0, m.start() - 20):m.start()])
                        if property_type == "Pozemek" or after_land_word:
                            area_land = val
                        else:
                            area_built_up = val
                except ValueError:
                    pass

        return area_built_up, area_land

    def _extract_photos(self, soup: BeautifulSoup, external_id: str) -> List[str]:
        """
        Sbírá URL fotek z detail stránky.

        Bazos vzor: https://www.bazos.cz/img/{N}/{last3}/{id}.jpg
        Thumbnaily: https://www.bazos.cz/img/{N}t/{last3}/{id}.jpg[?t=...]
        """
        last3 = str(external_id)[-3:]  # poslední 3 znaky ID (např. "271")
        id_str = external_id

        # Regex pro thumbnail i plnou fotku (Nt nebo N)
        img_re = re.compile(
            r"(?:https?:)?//(?:www\.)?bazos\.cz/img/(\d+)t?/"
            + re.escape(last3)
            + r"/"
            + re.escape(id_str)
            + r"\.jpg",
            re.IGNORECASE,
        )

        seen_indices: set[int] = set()
        for img in soup.find_all("img"):
            for attr in ("src", "data-src", "data-lazy", "data-original"):
                raw = str(img.get(attr, "")).split("?")[0]
                m = img_re.search(raw)
                if m:
                    seen_indices.add(int(m.group(1)))

        # Sestavit URL plných fotek, max 20
        return [
            f"{PHOTO_BASE}/img/{idx}/{last3}/{id_str}.jpg"
            for idx in sorted(seen_indices)[:20]
        ]

    # ── Save ───────────────────────────────────────────────────────────────────

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db_manager = get_db_manager()
        await db_manager.upsert_listing(listing)
