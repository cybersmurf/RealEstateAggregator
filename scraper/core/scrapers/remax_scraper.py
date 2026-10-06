"""
REMAX scraper for Czech real estate listings.

Optimalizovaný scraper s hybridním přístupem:
- httpx + BeautifulSoup pro list pages (rychlé)
- Playwright jen pro JS-heavy detail pages (pokud je potřeba)
"""
import asyncio
import logging
import re
import time
from typing import Any, List, Dict, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..browser import get_browser_manager
from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)


# Výpis podle filtru (typy bez vlastní adresy ve tvaru /reality/<typ>/prodej/…): okres je
# regions[116][<id>] – 116 = Jihomoravský kraj, 3713 = Znojmo, 3703 = Brno-venkov;
# types[92] = „Ostatní" (chaty a chalupy, garáže, vinné sklepy, zemědělské objekty),
# types[91] = komerční. Stejné karty i stránkování (&stranka=N) jako výpisy podle adresy.
_FILTER_URL = (
    "https://www.remax-czech.cz/reality/vyhledavani/"
    "?hledani=2&sale=1&types%5B{type_id}%5D=on&regions%5B116%5D%5B{district_id}%5D=on"
)

# Stav nabídky podle štítku / textu místo ceny
_SOLD_KEYWORDS = ("prodáno", "prodano", "pronajato")
_RESERVED_KEYWORDS = ("rezervováno", "rezervovano", "rezervace")

# Hodnota parametru „Typ nemovitosti" na detailu → náš typ (první shoda vyhrává)
_TYPE_PARAM_KEYWORDS = [
    ("chat", "Chata"), ("rekrea", "Chata"),          # „Chaty a rekreační objekty"
    ("garáž", "Garáž"),
    ("domy", "Dům"),                                  # „Domy a vily"
    ("byty", "Byt"),
    ("pozem", "Pozemek"),
    ("hotel", "Komerční"), ("penzion", "Komerční"),   # „Hotely, penziony a restaurace"
    ("restaur", "Komerční"), ("komerč", "Komerční"), ("kancel", "Komerční"),
    ("obchod", "Komerční"), ("sklad", "Komerční"), ("výrob", "Komerční"), ("zeměděl", "Komerční"),
]

# Záloha z titulku, když parametr chybí. Jen celá slova – „ubytovacího zařízení" není byt.
_TITLE_TYPE_PATTERNS = [
    (re.compile(r"\b(?:dům|domu|domy|vila|vily)\b"), "Dům"),
    (re.compile(r"\bbyt(?:u|y)?\b"), "Byt"),
    (re.compile(r"\bpozem(?:ek|ku|ky)\b"), "Pozemek"),
    (re.compile(r"\bchat[ay]\b|\bchalup"), "Chata"),
    (re.compile(r"\bgaráž"), "Garáž"),
    (re.compile(r"komerč|sklado|kancelář|provozov|obchodní|výrobní|restaurac|ubytovac|zeměděl"), "Komerční"),
]

_RE_LIST_TOTAL = re.compile(r"výsledky\s*\d+\s*-\s*(\d+)\s*z\s*celkem\s*(\d+)")


class RemaxScraper:
    """
    Scraper pro REMAX Czech Republic - okresy Znojmo a Brno-venkov.
    
    Strategy:
    1. List pages: httpx + BeautifulSoup (fast)
    2. Detail pages: httpx first, Playwright fallback pokud je JS required
    
    URL structure:
    - List: https://www.remax-czech.cz/reality/{category}/prodej/jihomoravsky-kraj/znojmo/?stranka=1
    - List (ostatní, komerční): /reality/vyhledavani/?…&types[92]=on&regions[116][3713]=on&stranka=1
    - Detail: https://www.remax-czech.cz/reality/detail/{id}/{slug}

    Okres: adresa na detailu ho většinou nenese ("ulice Dlouhá, Hrabětice"), takže ho každý
    inzerát dostává z výpisu, ve kterém byl nalezen (klíč "district" v SEARCH_CONFIGS).
    Bez toho geografický filtr zahazoval zhruba 40 % stažených inzerátů.
    """
    
    BASE_URL = "https://www.remax-czech.cz"
    SOURCE_CODE = "REMAX"
    
    # Znojmo + Brno-venkov, prodej: domy, pozemky, byty, ostatní (chaty, garáže…), komerční
    SEARCH_CONFIGS = [
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Dům",
            "district": "Znojmo",
        },
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/brno-venkov/",
            "offer_type": "Prodej",
            "property_type": "Dům",
            "district": "Brno-venkov",
        },
        {
            "url": "https://www.remax-czech.cz/reality/pozemky/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Pozemek",
            "district": "Znojmo",
        },
        {
            "url": "https://www.remax-czech.cz/reality/pozemky/prodej/jihomoravsky-kraj/brno-venkov/",
            "offer_type": "Prodej",
            "property_type": "Pozemek",
            "district": "Brno-venkov",
        },
        {
            "url": "https://www.remax-czech.cz/reality/byty/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej",
            "property_type": "Byt",
            "district": "Znojmo",
        },
        {
            "url": "https://www.remax-czech.cz/reality/byty/prodej/jihomoravsky-kraj/brno-venkov/",
            "offer_type": "Prodej",
            "property_type": "Byt",
            "district": "Brno-venkov",
        },
        {
            "url": _FILTER_URL.format(type_id=92, district_id=3713),
            "offer_type": "Prodej",
            "property_type": "Ostatní",
            "district": "Znojmo",
        },
        {
            "url": _FILTER_URL.format(type_id=92, district_id=3703),
            "offer_type": "Prodej",
            "property_type": "Ostatní",
            "district": "Brno-venkov",
        },
        {
            "url": _FILTER_URL.format(type_id=91, district_id=3713),
            "offer_type": "Prodej",
            "property_type": "Komerční",
            "district": "Znojmo",
        },
        {
            "url": _FILTER_URL.format(type_id=91, district_id=3703),
            "offer_type": "Prodej",
            "property_type": "Komerční",
            "district": "Brno-venkov",
        },
    ]
    
    def __init__(self, use_playwright_for_details: bool = False):
        """
        Args:
            use_playwright_for_details: Pokud True, použije Playwright i na detail pages
        """
        self.use_playwright_for_details = use_playwright_for_details
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None
    
    async def run(self, full_rescan: bool = False) -> int:
        """
        Hlavní entry point volaný z runner.py.
        
        Args:
            full_rescan: Pokud True, scrapuje všechny stránky, jinak jen prvních 5
            
        Returns:
            Počet úspěšně scrapnutých inzerátů
        """
        max_pages = 100 if full_rescan else 5
        total = 0
        for config in self.SEARCH_CONFIGS:
            count = await self.scrape(
                config["url"], config["offer_type"], config["property_type"],
                max_pages=max_pages, district=config.get("district"),
            )
            total += count
        return total
    
    async def scrape(
        self,
        search_url: str,
        offer_type: str,
        property_type: str,
        max_pages: int = 5,
        district: Optional[str] = None,
    ) -> int:
        """
        Projde jeden výpis (search config) a uloží jeho inzeráty.
        
        Args:
            max_pages: Maximální počet list pages k procházení (default 5 pro testing)
            district: Okres výpisu – zapíše se ke každému inzerátu
            
        Returns:
            Počet inzerátů uložených z TOHOTO výpisu (self.scraped_count je součet za celý běh –
            run() ho dřív sčítal jako výsledek každého výpisu a výsledek běhu tím nafukoval)
        """
        logger.info(f"Starting REMAX scraper for {search_url} (max_pages={max_pages})")
        count = 0
        
        with scraper_metrics_context() as metrics:
            # Reuse HTTP client pro všechny requesty
            async with httpx.AsyncClient(timeout=30, follow_redirects=True) as client:
                self._http_client = client
                
                page = 1
                
                while page <= max_pages:
                    url = self._page_url(search_url, page)
                    
                    try:
                        with timer(f"Fetch list page {page}"):
                            start = time.perf_counter()
                            html = await self._fetch_page_http(url)
                            metrics.record_fetch(time.perf_counter() - start)
                            
                        with timer(f"Parse list page {page}"):
                            start = time.perf_counter()
                            items = self._parse_list_page(html)
                            metrics.record_parse(time.perf_counter() - start)
                        
                        if not items:
                            logger.info(f"No more items on page {page}, stopping")
                            break

                        # Zpracuj items
                        for item in items:
                            # Předáme hint pro offer/property typ z URL konfigurace
                            item["offer_type_hint"] = offer_type
                            item["property_type_hint"] = property_type
                            item["district"] = district
                            try:
                                # Prodané má štítek už ve výpisu – detail není potřeba stahovat
                                if item.get("sold"):
                                    logger.info(f"Listing {item['external_id']} is sold – deactivating")
                                    await self._deactivate_listing(item["external_id"])
                                    metrics.increment_scraped()
                                    continue

                                # Fetch detail page pro kompletní data
                                detail_url = item["detail_url"]
                                detail_html = await self._fetch_page_http(detail_url)

                                # Prodáno → deaktivovat a neukládat; rezervaci si detail
                                # označí sám (price_note) a inzerát zůstává
                                if self._detect_status(detail_html) == "sold":
                                    logger.info(f"Listing {item['external_id']} is sold – deactivating")
                                    await self._deactivate_listing(item["external_id"])
                                    metrics.increment_scraped()
                                    continue

                                normalized = self._parse_detail_page(detail_html, item)
                                
                                await self._save_listing(normalized)
                                self.scraped_count += 1
                                count += 1
                                metrics.increment_scraped()
                                
                            except Exception as exc:
                                logger.error(f"Error processing item {item.get('title', 'N/A')}: {exc}")
                                metrics.increment_failed()

                        # Výpis sám říká, kolik má položek – na stránku za poslední se neptáme
                        if self._has_next_page(html) is False:
                            logger.info(f"Page {page} is the last one, stopping")
                            break

                        page += 1
                        await asyncio.sleep(1)  # Throttling - respektuj servery
                        
                    except Exception as exc:
                        logger.error(f"Error scraping page {page}: {exc}")
                        metrics.increment_failed()
                        break
                        
                self._http_client = None
        
        logger.info(f"REMAX list finished. Scraped {count} listings ({self.scraped_count} in this run so far)")
        return count

    @staticmethod
    def _page_url(search_url: str, page: int) -> str:
        """URL stránky výpisu – výpis podle filtru už dotazové parametry má."""
        separator = "&" if "?" in search_url else "?"
        return f"{search_url}{separator}stranka={page}"

    @staticmethod
    def _has_next_page(html: str) -> Optional[bool]:
        """
        Podle textu "Zobrazujeme výsledky 1-21 z celkem 26" pozná, jestli výpis pokračuje.
        None = text na stránce není (jiné rozložení) – pak se stránkuje do první prázdné stránky.
        """
        text = re.sub(r"<[^>]+>", " ", html)
        match = _RE_LIST_TOTAL.search(text)
        if not match:
            return None
        return int(match.group(1)) < int(match.group(2))

    @http_retry
    async def _fetch_page_http(self, url: str) -> str:
        """
        Stáhne HTML stránky pomocí httpx (fast).
        Při HTTP 429/503 nebo síťových chybách se automaticky opakuje (max 3×).
        """
        logger.debug(f"Fetching via HTTP: {url}")
        
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
            
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    @staticmethod
    def _status_from_text(text: str) -> Optional[str]:
        """"Prodáno" / "Pronajato" → "sold", "Rezervováno" → "reserved", jinak None."""
        low = text.lower()
        if any(kw in low for kw in _SOLD_KEYWORDS):
            return "sold"
        if any(kw in low for kw in _RESERVED_KEYWORDS):
            return "reserved"
        return None

    def _detect_status(self, html: str, soup: Optional[BeautifulSoup] = None) -> Optional[str]:
        """
        Stav nabídky na detailu: "sold" (Prodáno / Pronajato), "reserved" nebo None.

        REMAX stav ukazuje štítkem (.tags__item, u prodaných .tags__item--sold) a textem místo
        ceny v hlavičce (.pd-header__price = "Prodáno"). Dřív se prohledávaly všechny prvky
        s třídou obsahující "label"/"tag"/"status" – to by chytilo i popisek zaškrtávátka
        "rezervováno" ve vyhledávacím formuláři.
        """
        soup = soup or BeautifulSoup(html, "html.parser")
        texts = [el.get_text(" ", strip=True) for el in soup.select(".tags__item")]
        price_el = soup.select_one(".pd-header__price")
        if price_el:
            texts.append(price_el.get_text(" ", strip=True))

        statuses = {self._status_from_text(text) for text in texts}
        if "sold" in statuses:
            return "sold"
        if "reserved" in statuses:
            return "reserved"
        return None

    def _parse_list_page(self, html: str) -> List[Dict[str, Any]]:
        """
        Parsuje list stránku s inzeráty.
        
        Selektory jsou založené na skutečné struktuře REMAX webu (leden 2026).
        """
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []

        # Najdi všechny inzeráty (odkazy na detail)
        # REMAX používá <a href="/reality/detail/..."> strukturu
        for link in soup.select('a[href*="/reality/detail/"]'):
            href = link.get('href', '')
            if not href or '/reality/detail/' not in href:
                continue
                
            # Získej absolutní URL
            detail_url = urljoin(self.BASE_URL, href)
            
            # Extrahuj ID z URL (např. /reality/detail/423340/prodej-domu-123-m2-rakovnik)
            match = re.search(r'/reality/detail/(\d+)/', href)
            if not match:
                continue
            external_id = match.group(1)
            
            # Zkus najít title z textu odkazu nebo parent elementu
            title = link.get_text(strip=True)
            if not title:
                # Zkus parent element
                parent = link.find_parent()
                if parent:
                    title = parent.get_text(' ', strip=True)
            
            if not title or len(title) < 5:
                continue
            
            # Karta prodané nabídky: třída pl-items__item--sold + štítek "Prodáno" místo ceny
            sold = False
            card = link.find_parent(class_="pl-items__item")
            if card is not None:
                tags = " ".join(tag.get_text(" ", strip=True) for tag in card.select(".tags__item"))
                sold = (
                    "pl-items__item--sold" in card.get("class", [])
                    or self._status_from_text(tags) == "sold"
                )

            results.append({
                "source_code": self.SOURCE_CODE,
                "external_id": external_id,
                "detail_url": detail_url,
                "title": title[:200],  # Limit title length
                "sold": sold,
            })

        # Deduplikace podle external_id (u REMAX se často opakují odkazy)
        seen = set()
        unique_results = []
        for item in results:
            ext_id = item["external_id"]
            if ext_id not in seen:
                seen.add(ext_id)
                unique_results.append(item)

        logger.debug(f"Parsed {len(unique_results)} unique items from list page")
        return unique_results

    def _parse_detail_page(self, html: str, list_item: Dict[str, Any]) -> Dict[str, Any]:
        """
        Parsuje detail stránku inzerátu.

        Selektory jsou založené na skutečné struktuře REMAX detailu (březen 2026):
        - .pd-base-info__content-collapse-inner  → popis nemovitosti
        - .pd-detail-info__row                   → parametry (label + value)
        - .pd-header__address                    → adresa/lokace
        - .pd-header__price                      → cena
        - .pictogram__item[data-toggle=tooltip]  → ikony (pokoje, plochy)
        """
        soup = BeautifulSoup(html, "html.parser")

        result = {
            "source_code": self.SOURCE_CODE,
            "external_id": list_item["external_id"],
            "url": list_item["detail_url"],
        }

        # ── Title ──────────────────────────────────────────────────────────────
        title_el = soup.select_one('h2.pd-header__title') or soup.find('h1')
        if title_el:
            result["title"] = title_el.get_text(' ', strip=True)[:200]
        else:
            result["title"] = list_item.get("title", "")

        # ── Location ───────────────────────────────────────────────────────────
        # 1) Strukturovaná adresa z pd-header__address
        addr_el = soup.select_one('.pd-header__address')
        if addr_el:
            # Odstraň případný "mapa" odkaz z poslední části
            location_text = addr_el.get_text(' ', strip=True)
            location_text = re.sub(r'\s*mapa\s*$', '', location_text, flags=re.I).strip()
        else:
            # 2) Záloha: hint ze scrapnutého list_item
            location_text = list_item.get("location_text", "")
        # get_text nechává mezeru před čárkou tam, kde je v HTML zalomení
        location_text = re.sub(r"\s+,", ",", re.sub(r"\s+", " ", location_text)).strip()
        result["location_text"] = location_text[:200]

        # Okres z výpisu (adresa ho často nemá), obec z adresy
        if list_item.get("district"):
            result["district"] = list_item["district"]
        municipality = self._municipality_from_address(location_text)
        if municipality:
            result["municipality"] = municipality

        # ── Description ────────────────────────────────────────────────────────
        # Cílový selektor: .pd-base-info__content-collapse-inner
        desc_el = soup.select_one('.pd-base-info__content-collapse-inner')
        if desc_el:
            result["description"] = desc_el.get_text(' ', strip=True)[:5000]
        else:
            # Záloha: h4 perex + první obsáhlý odstavec
            parts = []
            h4 = soup.select_one('.pd-base-info__content h4')
            if h4:
                parts.append(h4.get_text(' ', strip=True))
            result["description"] = ' '.join(parts)[:5000]

        # ── Structured parameters from pd-detail-info__row ─────────────────────
        params: Dict[str, str] = {}
        for row in soup.select('.pd-detail-info__row'):
            label_el = row.select_one('.pd-detail-info__label')
            value_el = row.select_one('.pd-detail-info__value')
            if label_el and value_el:
                label = label_el.get_text(strip=True).rstrip(':').strip()
                value = value_el.get_text(' ', strip=True)
                params[label] = value

        # Užitná plocha / Plocha parcely
        if 'Užitná plocha' in params:
            m = re.search(r'(\d[\d\s]*)', params['Užitná plocha'])
            if m:
                result["area_built_up"] = float(m.group(1).replace(' ', '').replace('\xa0', ''))
        if 'Plocha parcely' in params:
            m = re.search(r'(\d[\d\s]*)', params['Plocha parcely'])
            if m:
                result["area_land"] = float(m.group(1).replace(' ', '').replace('\xa0', ''))

        # Stav objektu → condition
        if 'Stav objektu' in params:
            result["condition"] = params['Stav objektu']

        # Druh objektu → construction_type (Cihlová, Panel, Dřevostavba…)
        if 'Druh objektu' in params:
            result["construction_type"] = params['Druh objektu']

        # ── Disposition (pokoje) ───────────────────────────────────────────────
        # Zkus pictogram__item s title="Počet pokojů"
        for item in soup.select('.pictogram__item'):
            if item.get('title', '') == 'Počet pokojů':
                raw = item.get_text(strip=True)
                m = re.search(r'(\d+\+(?:\d+|kk))', raw, re.I)
                if m:
                    result["disposition"] = m.group(1).upper().replace('KK', 'kk')
                    break
        # Záloha: regex na titulek
        if 'disposition' not in result:
            disp_m = re.search(r'(\d+\+(?:\d+|kk))', result.get("title", ""), re.I)
            if disp_m:
                result["disposition"] = disp_m.group(1).upper().replace('KK', 'kk')

        # ── Price ──────────────────────────────────────────────────────────────
        price_el = soup.select_one('.pd-header__price')
        if price_el:
            price_text = price_el.get_text(' ', strip=True)
            price_m = re.search(r'([\d\s\xa0]+)\s*Kč', price_text)
            if price_m:
                try:
                    result["price"] = float(
                        price_m.group(1).replace(' ', '').replace('\xa0', '').replace('\u202f', '')
                    )
                except ValueError:
                    pass
        # Rezervace: nabídka zůstává, k ceně jde štítek; když web cenu nahradil textem,
        # upsert nechá poslední známou
        if self._detect_status(html, soup) == "reserved":
            result["price_note"] = "Rezervace"
            result["keep_last_price"] = True
        if 'price' not in result and price_el is None:
            # Záloha jen pro stránku bez hlavičky s cenou (jiné rozložení): první výskyt
            # "čísla Kč". Když hlavička je a číslo v ní není ("Cena na vyžádání v kanceláři",
            # "Rezervováno"), cena prostě není – první částka na stránce patří něčemu jinému.
            price_node = soup.find(string=re.compile(r'(\d[\d\s\xa0]+)\s*Kč'))
            if price_node:
                pm = re.search(r'([\d\s\xa0]+)\s*Kč', price_node)
                if pm:
                    try:
                        result["price"] = float(
                            pm.group(1).replace(' ', '').replace('\xa0', '').replace('\u202f', '')
                        )
                    except ValueError:
                        pass

        # ── Photos ────────────────────────────────────────────────────────────
        photo_urls = []
        for img in soup.find_all('img'):
            src = img.get('src', '')
            if 'mlsf.remax-czech.cz' in src or ('/data/' in src and 'remax' in src):
                photo_url = urljoin(self.BASE_URL, src)
                if photo_url not in photo_urls:
                    photo_urls.append(photo_url)
        result["photos"] = photo_urls[:50]

        # ── Property type ─────────────────────────────────────────────────────
        title_lower = result.get("title", "").lower()
        result["property_type"] = self._infer_property_type(
            title_lower,
            params.get('Typ nemovitosti', ''),
            list_item.get("property_type_hint", "Ostatní"),
        )

        # ── Offer type ────────────────────────────────────────────────────────
        if "pronájem" in title_lower or "pronajem" in title_lower:
            result["offer_type"] = "Pronájem"
        elif "prodej" in title_lower:
            result["offer_type"] = "Prodej"
        else:
            result["offer_type"] = list_item.get("offer_type_hint", "Prodej")

        return result

    @staticmethod
    def _infer_property_type(title_lower: str, type_param: str, hint: str) -> str:
        """
        Typ nemovitosti: parametr "Typ nemovitosti" z detailu, pak titulek, nakonec typ výpisu.

        Parametr má přednost – výpis "Ostatní" míchá chaty, garáže a vinné sklepy a titulek
        "Prodej ubytovacího zařízení" obsahuje "byt".
        """
        param_lower = type_param.lower()
        for keyword, property_type in _TYPE_PARAM_KEYWORDS:
            if keyword in param_lower:
                return property_type
        for pattern, property_type in _TITLE_TYPE_PATTERNS:
            if pattern.search(title_lower):
                return property_type
        return hint

    @staticmethod
    def _municipality_from_address(location_text: str) -> Optional[str]:
        """
        Obec z adresy detailu: "ulice Dlouhá, Hrabětice" → Hrabětice, "Višňové, okres Znojmo"
        → Višňové, "Strachotice – část obce Micmanice" → Strachotice.
        """
        parts = [p.strip() for p in location_text.split(",") if p.strip()]
        parts = [p for p in parts if not p.lower().startswith("okres ")]
        if not parts:
            return None
        municipality = re.split(r"\s+[–-]\s+", parts[-1])[0].strip()
        if not municipality or municipality.lower().startswith("ulice "):
            return None
        return municipality[:100]

    async def _deactivate_listing(self, external_id: str) -> None:
        """Prodaná nabídka: deaktivovat v DB (neukládá se)."""
        db = get_db_manager()
        await db.deactivate_listing(self.SOURCE_CODE, external_id)

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        """
        Uloží listing do databáze pomocí DatabaseManager.
        """
        try:
            db = get_db_manager()
            listing_id = await db.upsert_listing(listing)
            logger.info(f"Saved listing {listing_id}: {listing.get('title', 'N/A')[:50]} | {listing.get('price', 'N/A')} Kč")
        except Exception as exc:
            logger.error(f"Failed to save listing: {exc}")
            raise

