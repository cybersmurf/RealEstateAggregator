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

# Parametry hledání (Znojmo, 25 km, max 8.5M Kč)
_SEARCH_PARAMS = "hledat=&hlokalita=66902&humkreis=25&cenaod=&cenado=8500000&order="
_SEARCH_URL_P1 = (
    f"{BASE_URL}/?hledat=&rubriky=reality&hlokalita=66902"
    "&humkreis=25&cenaod=&cenado=8500000&Submit=Hledat"
)
_SEARCH_URL_PAGED = f"{BASE_URL}/{{offset}}/?{_SEARCH_PARAMS}"

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


class BazosScraper:
    """Scraper pro reality.bazos.cz (Znojmo, 25 km okruh, max 8.5M Kč)."""

    SOURCE_CODE = "BAZOS"
    _MAX_PAGES_INCREMENTAL = 3    # ~60 nejnovějších inzerátů
    _MAX_PAGES_FULL = 30          # max ~600 inzerátů (aktuálně ~431 výsledků)

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = self._MAX_PAGES_FULL if full_rescan else self._MAX_PAGES_INCREMENTAL
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting Bazos.cz scraper (max_pages=%s)", max_pages)

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client

                page = 1
                total_scraped = 0
                seen_ids: set[str] = set()

                while page <= max_pages:
                    url = _SEARCH_URL_P1 if page == 1 else _SEARCH_URL_PAGED.format(offset=(page - 1) * 20)

                    try:
                        with timer(f"Bazos list page {page}", logging.DEBUG):
                            start = time.perf_counter()
                            html = await self._fetch(url)
                            metrics.record_fetch(time.perf_counter() - start)

                        items, has_next = self._parse_list_page(html)
                        if not items:
                            logger.info("Bazos: no items on page %s, stopping", page)
                            break

                        new_items = [i for i in items if i["external_id"] not in seen_ids]
                        for it in new_items:
                            seen_ids.add(it["external_id"])

                        logger.info(
                            "Bazos page %s: %s new items (total found so far: %s)",
                            page, len(new_items), len(seen_ids),
                        )

                        for item in new_items:
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

                                await self._save_listing(listing)
                                total_scraped += 1
                                metrics.increment_scraped()
                                await asyncio.sleep(0.5)

                            except Exception as exc:
                                logger.error(
                                    "Bazos: error processing detail %s: %s",
                                    item.get("detail_url"), exc,
                                )
                                metrics.increment_failed()

                        if not has_next:
                            logger.info("Bazos: last page reached at page %s", page)
                            break

                        page += 1
                        await asyncio.sleep(1.0)  # zdvořilé crawlování

                    except Exception as exc:
                        logger.error("Bazos: error fetching list page %s (%s): %s", page, url, exc)
                        break

        self.scraped_count = total_scraped
        logger.info("Bazos.cz scraper finished. Scraped %s listings", total_scraped)
        return total_scraped

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── List page ──────────────────────────────────────────────────────────────

    def _parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], bool]:
        """
        Zparsuje stránku seznamu inzerátů.

        Returns:
            (items, has_next_page)
        """
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen_ids: set[str] = set()

        _LISTING_RE = re.compile(r"/inzerat/(\d+)/")

        for a in soup.find_all("a", href=True):
            href = str(a.get("href", ""))
            m = _LISTING_RE.search(href)
            if not m:
                continue

            # Přeskočit fotogalerní odkazy (obsahují <img>) – hledáme textový odkaz
            if a.find("img"):
                continue

            external_id = m.group(1)
            if external_id in seen_ids:
                continue
            seen_ids.add(external_id)

            # Celé URL detailu (bez query parametrů)
            raw_path = href.split("?")[0]
            if raw_path.startswith("http"):
                detail_url = raw_path
            else:
                detail_url = BASE_URL + raw_path

            # Titulek z textu odkazu (nebo rodičovského nadpisu)
            title = a.get_text(" ", strip=True)
            if not title:
                parent = a.find_parent(["h2", "h3", "div"])
                if parent:
                    title = parent.get_text(" ", strip=True)

            # Cena z okolního kontextu
            price_text = ""
            container = a.find_parent()
            if container:
                ctx = container.get_text(" ", strip=True)
                pm = re.search(r"([\d][\d\s\xa0]{3,})\s*Kč", ctx)
                if pm:
                    price_text = pm.group(0).strip()

            results.append({
                "external_id": external_id,
                "detail_url": detail_url,
                "title": title[:200],
                "price_text": price_text,
            })

        # Detekce další stránky: odkaz s offset /NN/?hledat=
        has_next = bool(soup.find("a", href=re.compile(r"/\d+/\?hledat=")))

        return results, has_next

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
        # Bazos.cz používá <div class="popis"> pro popis inzerátu
        for selector in (".popis", "#popis", ".inzerat-popis", ".detailpopis"):
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
        m_land = re.search(
            r"(?:pozemk(?:u|em|y|ů|a)|zahrad(?:a|e|y|ou)|parcel)\s+(?:o\s+(?:výměře|velikosti|ploše)\s+)?([\d][\d\s\.]+)\s*(?:m[²2]|㎡)",
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
        m_built = re.search(
            r"(?:zastav[eě]n[oaáíé]+\s+(?:plochou?|plochem?)|u[žz]itn[aáíé]+\s+(?:plochou?|plochem?))[^\d]*(\d{2,5})\s*(?:m[²2]|㎡)",
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

        # Fallback: první 'X m²' v POPISU (ne titulku), aby titulek nefalšoval výsledek
        if area_built_up is None:
            m = re.search(r"(\d{2,5})\s*(?:m[²2]|㎡)", description)
            if m:
                try:
                    val = float(m.group(1))
                    if 10 <= val <= 5000:
                        if property_type == "Pozemek" and area_land is None:
                            area_land = val
                        else:
                            area_built_up = val
                except ValueError:
                    pass

        # Pokud pořád nic, zkus titulek – ale jen pro Pozemek kde to jde do area_land
        if area_built_up is None and area_land is None:
            m = re.search(r"(\d{2,5})\s*(?:m[²2]|㎡)", title)
            if m:
                try:
                    val = float(m.group(1))
                    if 10 <= val <= 100000:
                        if property_type == "Pozemek":
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
