"""
NemovitostiZnojmo.cz scraper (Eurobydleni/Urbium platform).
Strategie: httpx + BeautifulSoup, SSR stránky
Paginace: /reality/page-N
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

BASE_URL = "https://www.nemovitostiznojmo.cz"
LIST_URL = f"{BASE_URL}/reality/"

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": BASE_URL,
}

# Pořadí rozhoduje (první shoda vyhrává): "Prodej vily 5+1 s garáží" je dům, ne garáž.
# Kmeny místo tvarů – "pozemků", "parcely" na "pozemek"/"parcela" nesedí.
PROPERTY_TYPE_MAP = {
    "byt": "Byt", "byty": "Byt",
    "dům": "Dům", "dom": "Dům", "rodinný": "Dům", "vil": "Dům",
    "chat": "Chata", "chalup": "Chata",
    "pozem": "Pozemek", "parcel": "Pozemek",
    "garáž": "Garáž", "garážové": "Garáž",
    "komerční": "Komerční", "ostatní": "Ostatní",
    "sklep": "Ostatní", "vinný": "Ostatní",
}

# Typ podle popisku, který web uvádí sám: na detailu řádky „Typ nemovitosti" + „Upřesnění"
# („Domy" + „Rodinný dům"), ve výpisu první položka řádku .portfolio--text--main
# („Rodinný dům, konstrukce: …"; u bytů dispozice „4+kk, Osobní, …"). První shoda vyhrává:
# pozemek před komerčním („Pozemek, komerční"), chata před domem („Domy" + „Chata").
SITE_TYPE_PATTERNS = [
    (re.compile(r"^\d\s*\+\s*(?:kk|\d)|atypick|garson|\bbyt"), "Byt"),
    (re.compile(r"pozem|parcel|zahrad|\bpole\b|\blouk|\bles\b"), "Pozemek"),
    (re.compile(r"chat[ay]?\b|chalup"), "Chata"),
    (re.compile(r"rodinn|v[íi]cegenera|\bvil[ay]\b|[čc]in[žz]ovn|\bd[ůu]m\b|\bdomy\b"), "Dům"),
    (re.compile(r"gar[áa][žz]"), "Garáž"),
    (re.compile(r"sklep"), "Ostatní"),
    (re.compile(r"obchod|kancel|ordinac|sklad|v[ýy]rob|administrativ|hotel|pen[sz]ion|restaur|provozn|komer[čc]|ubytov"), "Komerční"),
]


class NemovitostiZnojmoScraper:
    """Scraper pro nemovitostiznojmo.cz (Eurobydleni/Urbium platforma)."""

    SOURCE_CODE = "NEMZNOJMO"

    def __init__(self) -> None:
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 50 if full_rescan else 5
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 5) -> int:
        logger.info("Starting NemovitostiZnojmo scraper (max_pages=%s)", max_pages)
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=30,
                follow_redirects=True,
                headers=DEFAULT_HEADERS,
            ) as client:
                self._http_client = client
                page = 1
                while page <= max_pages:
                    url = LIST_URL if page == 1 else f"{LIST_URL}page-{page}"
                    try:
                        with timer(f"Fetch list page {page}"):
                            start = time.perf_counter()
                            html = await self._fetch(url)
                            metrics.record_fetch(time.perf_counter() - start)

                        items, has_next = self._parse_list_page(html)
                        if not items:
                            logger.info("No items on page %s, stopping", page)
                            break

                        logger.info("Page %s: found %s listings", page, len(items))

                        for item in items:
                            try:
                                detail_html = await self._fetch(item["url"])
                                normalized = self._parse_detail_page(detail_html, item)
                                if normalized.pop("sold", False):
                                    # Prodaná / pronajatá nabídka, kterou web ještě ukazuje – u nás aktivní být nesmí
                                    logger.info("Listing %s is sold – deactivating", normalized["external_id"])
                                    await get_db_manager().deactivate_listing(self.SOURCE_CODE, normalized["external_id"])
                                    self.scraped_count += 1
                                    await asyncio.sleep(0.5)
                                    continue
                                await self._save_listing(normalized)
                                self.scraped_count += 1
                                metrics.increment_scraped()
                                await asyncio.sleep(0.5)
                            except Exception as exc:
                                logger.error("Error processing %s: %s", item.get("url"), exc)
                                metrics.increment_failed()

                        if not has_next:
                            break
                        page += 1
                        await asyncio.sleep(1.0)

                    except httpx.HTTPStatusError as exc:
                        logger.error("HTTP error page %s: %s", page, exc)
                        break
                    except Exception as exc:
                        logger.error("Error page %s: %s", page, exc)
                        metrics.increment_failed()
                        break

        self._http_client = None
        logger.info("NemovitostiZnojmo scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_list_page(self, html: str) -> Tuple[List[Dict[str, Any]], bool]:
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        # Eurobydleni: listing items jsou <a href="/slug/detail/ID">
        for link in soup.select("a[href*='/detail/']"):
            href = link.get("href", "")
            if not re.search(r"/detail/\d+$", href):
                continue
            full_url = urljoin(BASE_URL, href)
            # Deduplicate
            if any(r["url"] == full_url for r in results):
                continue

            title_el = link.select_one(".portfolio--headline, h2, h3, h4, .title, strong")
            title = title_el.get_text(" ", strip=True) if title_el else ""

            price_text = ""
            for el in link.find_all(string=re.compile(r"Kč|Kc")):
                stripped = el.strip()
                if re.search(r"\d", stripped) and len(stripped) < 50:
                    price_text = stripped
                    break

            # Popisek typu („Rodinný dům, konstrukce: Cihlová, …") – první položka před čárkou
            label_el = link.select_one(".portfolio--text--main")
            site_type = label_el.get_text(" ", strip=True).split(",")[0].strip() if label_el else ""

            # Štítek „Rezervováno" je na každé fotce karty (.portfolio--reserved), karta má třídu js-reserved
            card = link.find_parent(class_="portfolio--column")
            reserved = bool(card and (card.select_one(".portfolio--reserved") or "js-reserved" in (card.get("class") or [])))

            results.append({
                "url": full_url,
                "title": title[:200],
                "price_text": price_text,
                "site_type": site_type[:100],
                "reserved": reserved,
            })

        # Paginace: číslované odkazy na stránky
        has_next = bool(soup.select("a[href*='page-']"))
        # Kontrola aktivní stránky vs. dostupné
        current_page_links = soup.select("nav a, .pagination a, [class*='page'] a")
        if has_next:
            # Ověřit, že existuje stránka vyšší než aktuální
            page_nums = []
            for a in current_page_links:
                m = re.search(r"page-(\d+)", a.get("href", ""))
                if m:
                    page_nums.append(int(m.group(1)))
            if not page_nums:
                has_next = False

        return results, has_next

    def _parse_price(self, price_text: str) -> Optional[float]:
        if not price_text:
            return None
        match = re.search(r'(\d[\d\s]+)', price_text)
        if match:
            price_str = match.group(1).replace(' ', '').replace('\xa0', '')
            try:
                return float(price_str)
            except ValueError:
                return None
        return None

    @staticmethod
    def _parse_tables(soup: BeautifulSoup) -> Dict[str, str]:
        """Řádky tabulek detailu (<td>popisek</td><th>hodnota</th>) jako {popisek malými: hodnota}.

        Souhrn nahoře má popisky s dvojtečkou („Adresa:" = ulice a PSČ) a nesmí přepsat stejně
        pojmenované řádky podrobných bloků („Adresa" = obec) – proto se řádky s dvojtečkou vynechají.
        """
        params: Dict[str, str] = {}
        for row in soup.select("table.detail-main--table tr, table.table--info tr"):
            cells = row.find_all(["td", "th"])
            if len(cells) < 2:
                continue
            label = cells[0].get_text(" ", strip=True)
            if not label or label.endswith(":"):
                continue
            params.setdefault(label.lower(), cells[1].get_text(" ", strip=True))
        return params

    @staticmethod
    def _type_from_site_label(label: str) -> Optional[str]:
        """Typ nemovitosti z popisku webu („Rodinný dům", „4+kk", „Pozemek, komerční", „Domy Chata")."""
        text = (label or "").strip().lower()
        if not text:
            return None
        for pattern, property_type in SITE_TYPE_PATTERNS:
            if pattern.search(text):
                return property_type
        return None

    @staticmethod
    def _type_from_title(title_lower: str) -> str:
        """Záloha, když web typ neuvede: klíčová slova z názvu (první shoda v PROPERTY_TYPE_MAP)."""
        for keyword, ptype in PROPERTY_TYPE_MAP.items():
            if keyword in title_lower:
                return ptype
        return "Ostatní"

    def _parse_detail_page(
        self, html: str, list_item: Dict[str, Any]
    ) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "url": list_item["url"],
        }

        # External ID z URL
        match = re.search(r"/detail/(\d+)$", list_item["url"])
        if match:
            result["external_id"] = match.group(1)
        else:
            # Fallback
            result["external_id"] = list_item["url"].split("/")[-1]

        title_el = soup.find("h1") or soup.find("h2")
        result["title"] = (
            title_el.get_text(" ", strip=True)[:200] if title_el else list_item.get("title", "")
        )

        # Cena
        price_text = ""
        for el in soup.find_all(string=lambda t: t and "Kč" in t):
            stripped = el.strip()
            if re.search(r"\d", stripped) and len(stripped) < 50:
                price_text = stripped
                break
        result["price"] = self._parse_price(price_text)

        params = self._parse_tables(soup)

        # Typ nemovitosti: nejdřív to, co uvádí web (tabulka detailu, popisek z výpisu), teprve pak
        # klíčová slova z názvu. Jen podle názvu skončil „Dům v centru Dyjákovic … nebo 3 byty"
        # mezi byty a hotely s „ubytováním" taky („byt" je první klíč mapy).
        title_lower = result["title"].lower()
        result["property_type"] = (
            self._type_from_site_label(f'{params.get("typ nemovitosti", "")} {params.get("upřesnění", "")}')
            or self._type_from_site_label(list_item.get("site_type", ""))
            or self._type_from_title(title_lower)
        )

        # Offer type – řádek „Typ prodeje" (prodej / pronájem), záložně název
        sale_type = params.get("typ prodeje", "").lower()
        if "pron" in sale_type or "pronájem" in title_lower or "pronajm" in title_lower:
            result["offer_type"] = "Pronájem"
        else:
            result["offer_type"] = "Prodej"

        # Stav nabídky: řádek „Stav inzerátu" („Rezervováno, jen vlastní web"), štítek na fotce
        # detailu, nebo štítek karty ve výpisu. Rezervovaná nabídka zůstává vidět s poznámkou –
        # 6. 10. 2026 jich bylo 8 ze 100 a u nás vypadaly jako volné.
        state = params.get("stav inzerátu", "").lower()
        if state.startswith(("prodán", "pronaj")):
            result["sold"] = True
        elif state.startswith("rezerv") or soup.select_one(".detail-slider--reserved") or list_item.get("reserved"):
            result["price_note"] = "Rezervace"
            result["keep_last_price"] = True

        # Obec: řádek „Adresa" (bez dvojtečky) v bloku „Adresa, lokalita, GPS" – „Chvalovice", u části
        # města „Znojmo, Načeratice" (obec, část obce). „Přesná adresa" je naproti tomu „č.p. 160,
        # 66902 Znojmo" nebo „Parcela 3070, Načeratice" – podle ní geografický filtr novostavby
        # v Načeraticích zahazoval, i když patří ke Znojmu.
        municipality = params.get("adresa", "").split(",")[0].strip()
        if municipality and not re.search(r"\d", municipality):
            result["municipality"] = municipality[:100]

        # Popis
        desc_el = soup.select_one(".description, article .content, main p, .perex")
        result["description"] = desc_el.get_text(" ", strip=True)[:5000] if desc_el else ""

        # Parametry (Eurobydleni)
        # Hledáme tabulku nebo seznam parametrů
        for row in soup.select("tr, li, .param-item"):
            text = row.get_text(" ", strip=True).lower()
            
            # Plocha
            if "plocha" in text and ("m2" in text or "m²" in text):
                match = re.search(r'(\d+)\s*m[²2]', text)
                if match:
                    area_val = float(match.group(1))
                    if "pozem" in text or "parcel" in text:
                        result["area_land"] = area_val
                    else:
                        result["area_built_up"] = area_val
            
            # Lokace – HTML Eurobydleni má label a hodnotu v jednom elementu,
            # např. "Přesná adresa Božice, Znojmo" nebo "Adresa Božice".
            # Stripujeme label a preferujeme delší hodnotu (s okresem > bez okresu).
            if "lokalita" in text or "adresa" in text or "obec" in text:
                # Odstraň prefixový label: "přesná adresa", "adresa:", "lokalita", "obec"
                clean_text = re.sub(
                    r'^(?:p[řr]esn[áa]\s+)?(?:lokalita|adresa|obec|m[íi]sto):?\s*',
                    '', text, flags=re.I,
                ).strip()
                # Fallback na strukturovaný val_el (ne vždy existuje)
                val_el = row.select_one("td:nth-child(2), span.value, strong")
                if val_el:
                    val_text = val_el.get_text(strip=True)
                    if len(val_text) > len(clean_text):
                        clean_text = val_text
                if clean_text and len(clean_text) > 3:
                    existing = result.get("location_text", "")
                    if len(clean_text) > len(existing):
                        result["location_text"] = clean_text[:200]

            # GPS souřadnice přímo z textu řádku (ušetří Nominatim geocoding)
            if "gps latitude" in text or "zeměpisná šířka" in text:
                m = re.search(r'(\d{2,3}[.,]\d+)', text)
                if m:
                    try:
                        result["latitude"] = float(m.group(1).replace(",", "."))
                    except ValueError:
                        pass
            if "gps longitude" in text or "zeměpisná délka" in text:
                m = re.search(r'(\d{1,3}[.,]\d+)', text)
                if m:
                    try:
                        result["longitude"] = float(m.group(1).replace(",", "."))
                    except ValueError:
                        pass

        # Pokud jsme nenašli lokaci v parametrech, zkusíme najít v textu
        if "location_text" not in result or not result["location_text"]:
            loc_candidates = soup.find_all(string=re.compile(r'Znojmo|okres Znojmo', re.I))
            if loc_candidates:
                result["location_text"] = loc_candidates[0].strip()[:200]
            else:
                result["location_text"] = "Znojmo a okolí"

        # Fotky - eurobydleni platforma: class="detail-slider--img" a "gallery--img"
        photo_urls = []
        for img in soup.select("img.detail-slider--img, img.gallery--img, .gallery img, .fotorama img"):
            src = img.get("src") or img.get("data-src") or ""
            if src.startswith("//"):
                src = "https:" + src
            if src.startswith("http") and src not in photo_urls and "logo" not in src.lower():
                photo_urls.append(src)

        # Fallback pro fotky, pokud selektory selžou
        if not photo_urls:
            for img in soup.find_all("img"):
                src = img.get("src", "")
                if src.startswith("//"):
                    src = "https:" + src
                if ("eurobydleni.cz/rozhrani/uploads" in src
                        or "foto" in src.lower() or "gallery" in src.lower()):
                    if src not in photo_urls:
                        photo_urls.append(src)

        result["photos"] = photo_urls[:50]

        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        try:
            db = get_db_manager()
            listing_id = await db.upsert_listing(listing)
            logger.info(f"Saved listing {listing_id}: {listing.get('title', 'N/A')[:50]} | {listing.get('price', 'N/A')} Kč")
        except Exception as exc:
            logger.error(f"Failed to save listing: {exc}")
            raise
