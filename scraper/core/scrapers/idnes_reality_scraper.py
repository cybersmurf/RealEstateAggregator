"""
Idnes Reality scraper for Czech real estate listings.

Strategie: výpis podle okresu + detail (SSR, httpx + BeautifulSoup, bez Playwrightu).

- Výpis `https://reality.idnes.cz/s/{okres}/?page=N` (N od 0, 26 položek na stránku, za poslední
  stránkou 404) vrací všechny nabídky okresu: Znojmo `okres-znojmo`, Brno-venkov `brno-venkov`.
  6. 10. 2026: 1 198 + 1 733 nemovitostí. robots.txt výpis i stránkování povoluje
  (zakazuje jen kombinace s řazením, cenou a více hodnotami).
- Položka výpisu nese adresu detailu, cenu a lokalitu „Ulice, Obec, okres X" (okresní město bez
  „okres") – odtud bereme obec i okres, takže odpadá hádání z adresy.
- Detail se stahuje jen u nových inzerátů a při změně ceny; známým se obnoví „naposledy viděno".
  Jinak by plný běh znamenal ~3 000 detailů za noc (2,5 h, limit úlohy je 45 min).

Do 6. 10. 2026 se inzeráty hledaly v sitemapě podle 13 názvů obcí v URL: pokrývalo to jen město
Znojmo a pár obcí (370 z ~2 900 nabídek) a slug „miroslav" pouštěl dovnitř pražskou ulici
Miroslava Hájka.
"""
import asyncio
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..http_utils import http_retry

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..filters import get_filter_manager
from ..area_parsing import parse_title_areas

logger = logging.getLogger(__name__)


class IdnesRealityScraper:
    """Scraper for reality.idnes.cz (Czech News Agency real estate portal)."""

    BASE_URL = "https://reality.idnes.cz"
    SOURCE_CODE = "IDNES"

    # Okres → část adresy výpisu (/s/{slug}/)
    DISTRICT_SEARCH: Dict[str, str] = {
        "Znojmo": "okres-znojmo",
        "Brno-venkov": "brno-venkov",
    }
    MAX_LIST_PAGES = 200            # pojistka; 26 položek na stránku
    INCREMENTAL_LIST_PAGES = 3      # inkrementální běh: jen první stránky každého okresu
    MAX_DETAILS_PER_RUN = 800       # ~2,4 s na detail → vejde se do 45min limitu úlohy

    def __init__(self):
        """Initialize the scraper."""
        self.scraped_count = 0
        self.skipped_other_district = 0
        # False = některou stránku výpisu se nepodařilo načíst; co jsme neviděli, nemusí být stažené
        self.lists_complete = True
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """
        Main entry point called from runner.py.

        Returns:
            Počet inzerátů viděných ve výpisech (obnovené + nově stažené) – runner podle něj
            pozná, že běh proběhl, a po plném rescanu deaktivuje ty, které ve výpisu nebyly.
        """
        max_list_pages = self.MAX_LIST_PAGES if full_rescan else self.INCREMENTAL_LIST_PAGES
        return await self.scrape(max_list_pages=max_list_pages)

    async def scrape(self, max_list_pages: int = INCREMENTAL_LIST_PAGES) -> int:
        logger.info(f"Starting Idnes Reality scraper (max_list_pages={max_list_pages})")

        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(
                timeout=60,   # stránky výpisu odpovídají 2–5 s, občas výrazně déle
                follow_redirects=True,
            ) as client:
                self._http_client = client

                try:
                    db = get_db_manager()
                    known = await db.get_known_prices(self.SOURCE_CODE)

                    with timer("Fetch iDNES district lists"):
                        items = await self._fetch_district_items(max_list_pages)
                    if not items:
                        logger.warning("No listings found in iDNES district lists")
                        return 0

                    # Známé inzeráty stačí „vidět" (a doplnit jim obec a okres z výpisu)
                    touched = await db.touch_listings(
                        self.SOURCE_CODE,
                        [(i["external_id"], i["municipality"], i["district"]) for i in items if i["external_id"] in known],
                    )

                    # Neúplný výpis: runner po plném běhu deaktivuje vše, co jsme „neviděli" –
                    # aktivní inzeráty proto necháme viděné a o stažení rozhodne až úplný běh.
                    if not self.lists_complete:
                        kept = await db.mark_active_seen(self.SOURCE_CODE)
                        logger.warning(f"iDNES lists incomplete – {kept} active listings kept as seen, nothing will be deactivated")

                    details = [i for i in self.select_for_detail(items, known) if self.passes_filters(i)]
                    logger.info(
                        f"iDNES lists: {len(items)} listings, {touched} known refreshed, "
                        f"{len(details)} need detail (limit {self.MAX_DETAILS_PER_RUN}), "
                        f"{self.skipped_other_district} skipped as other district"
                    )

                    count = 0
                    for idx, item in enumerate(details[: self.MAX_DETAILS_PER_RUN]):
                        try:
                            with timer(f"Fetch detail {idx + 1}/{min(len(details), self.MAX_DETAILS_PER_RUN)}"):
                                detail_html = await self._fetch_page(item["url"])

                            normalized = self._parse_detail_page(detail_html, item["url"])
                            if normalized:
                                self.apply_list_item(normalized, item)
                                await self._save_listing(normalized)
                                count += 1
                                metrics.increment_scraped()

                        except Exception as exc:
                            logger.error(f"Error processing listing {item['url']}: {exc}")
                            metrics.increment_failed()

                        # Throttling
                        await asyncio.sleep(0.5)

                    self.scraped_count = touched + count

                except Exception as exc:
                    logger.error(f"Scraping failed: {exc!r}")
                    metrics.increment_failed()

                finally:
                    self._http_client = None

        logger.info(f"Idnes Reality scraper finished. Scraped {self.scraped_count} listings")
        return self.scraped_count

    async def _fetch_district_items(self, max_list_pages: int) -> List[Dict[str, Any]]:
        """Projde výpisy okresů a vrátí položky (bez duplicit), každou s obcí a okresem."""
        items: List[Dict[str, Any]] = []
        seen: set[str] = set()

        for district, slug in self.DISTRICT_SEARCH.items():
            for page in range(min(max_list_pages, self.MAX_LIST_PAGES)):
                url = f"{self.BASE_URL}/s/{slug}/" + (f"?page={page}" if page else "")
                try:
                    html = await self._fetch_page(url)
                except httpx.HTTPStatusError as exc:
                    if exc.response.status_code == 404:
                        break  # za poslední stránkou
                    logger.error(f"iDNES list {url} failed: {exc!r}")
                    self.lists_complete = False
                    break
                except (httpx.TimeoutException, httpx.TransportError) as exc:
                    # Jedna nedostupná stránka nesmí shodit celý běh (6. 10. 2026: ReadTimeout
                    # na 45. stránce Brna-venkova = 0 inzerátů za celý běh)
                    logger.error(f"iDNES list {url} failed: {exc!r}")
                    self.lists_complete = False
                    break

                page_items = self.parse_list_page(html, district)
                if not page_items["items"] and not page_items["other_district"]:
                    break
                self.skipped_other_district += page_items["other_district"]
                for item in page_items["items"]:
                    if item["external_id"] not in seen:
                        seen.add(item["external_id"])
                        items.append(item)

                await asyncio.sleep(0.5)

            logger.info(f"iDNES district {district}: {len(items)} listings so far")

        return items

    # ── čisté funkce (testovatelné bez HTTP a DB) ────────────────────────────

    _RE_LIST_PRICE = re.compile(r"(\d[\d\s.]*)\s*(?:Kč|CZK)")

    @classmethod
    def parse_list_page(cls, html: str, district: str) -> Dict[str, Any]:
        """
        Stránka výpisu → {"items": [...], "other_district": N}.

        Položka: url, external_id, location_text, municipality, district, price.
        Reklamní bloky se přeskakují; nabídka s lokalitou z jiného okresu (zvýrazněné „tipy")
        se nepočítá mezi položky.
        """
        soup = BeautifulSoup(html, "html.parser")
        items: List[Dict[str, Any]] = []
        other_district = 0

        for node in soup.select("div.c-products__item"):
            if "c-products__item-advertisment" in (node.get("class") or []):
                continue
            link = node.select_one("a.c-products__link[href]")
            href = str(link.get("href", "")) if link else ""
            if "/detail/" not in href:
                continue
            url = urljoin(cls.BASE_URL, href)

            info_el = node.select_one(".c-products__info")
            info = " ".join(info_el.get_text(" ", strip=True).split()) if info_el else ""
            location = cls.parse_list_location(info, district)
            if location is None:
                other_district += 1
                continue

            price_el = node.select_one(".c-products__price")
            items.append({
                "url": url,
                "external_id": cls._extract_external_id(url),
                "location_text": info,
                "municipality": location,
                "district": district,
                "price": cls.parse_list_price(price_el.get_text(" ", strip=True) if price_el else ""),
            })

        return {"items": items, "other_district": other_district}

    @staticmethod
    def parse_list_location(info: str, district: str) -> Optional[str]:
        """
        „Hlavní, Šanov, okres Znojmo" → obec „Šanov"; okresní město se píše bez okresu
        („Vančurova, Znojmo"). None = lokalita chybí nebo patří do jiného okresu.
        """
        parts = [p.strip() for p in info.split(",") if p.strip()]
        if not parts:
            return None
        if parts[-1].lower().startswith("okres "):
            if parts[-1][6:].strip() != district:
                return None
            parts = parts[:-1]
            if not parts:
                return None
        elif parts[-1] != district:
            return None
        # „Hostěradice - Chlupice" = obec - část obce
        return parts[-1].split(" - ")[0].strip() or None

    @classmethod
    def parse_list_price(cls, text: str) -> Optional[float]:
        """„5 990 000 Kč", „14 000 Kč/měsíc", „65 000 Kč (92 Kč/m²)" → číslo; „Info o ceně u RK" → None."""
        clean = text.replace("\u200d", "").replace("\u00a0", " ")
        match = cls._RE_LIST_PRICE.search(clean)
        if not match:
            return None
        digits = re.sub(r"[^\d]", "", match.group(1))
        return float(digits) if digits else None

    @staticmethod
    def select_for_detail(items: List[Dict[str, Any]], known: Dict[str, Optional[float]]) -> List[Dict[str, Any]]:
        """
        Které položky potřebují detail: napřed známé se změněnou cenou (ať se zapíše do historie),
        potom nové. Známé se stejnou cenou detail nepotřebují.
        """
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

    @staticmethod
    def types_from_url(url: str) -> Tuple[str, str]:
        """
        (typ nemovitosti, typ nabídky) z adresy detailu /detail/{prodej|pronajem|drazba}/{typ}/…
        Dražby se dřív ukládaly jako prodej.
        """
        url_lower = url.lower()
        property_type = "Other"
        if "/byt/" in url_lower or "/byt-" in url_lower:
            property_type = "Apartment"
        elif "/dum/" in url_lower or "/dum-" in url_lower or "/domy/" in url_lower:
            property_type = "House"
        elif "/pozemek/" in url_lower or "/pozemek-" in url_lower:
            property_type = "Land"
        elif "/chata/" in url_lower or "/chalupa/" in url_lower or "/chata-" in url_lower:
            property_type = "Cottage"
        elif "/komercni" in url_lower or "/komerci" in url_lower:
            property_type = "Commercial"
        elif "/garaz/" in url_lower or "/garaz-" in url_lower:
            property_type = "Garage"

        if "/pronajem/" in url_lower:
            offer_type = "Rent"
        elif "/drazba/" in url_lower:
            offer_type = "Auction"
        else:
            offer_type = "Sale"
        return property_type, offer_type

    @classmethod
    def passes_filters(cls, item: Dict[str, Any]) -> bool:
        """
        Projde položka výpisu cenovými a typovými filtry (settings.yaml)? Co by upsert stejně
        zahodil (pozemek nad limit), nemá smysl stahovat – a stahovalo by se každou noc znovu,
        protože se to nikdy neuloží.
        """
        property_type, offer_type = cls.types_from_url(item["url"])
        return get_filter_manager().passes_search_filters({
            "property_type": property_type,
            "offer_type": offer_type,
            "price": item.get("price"),
            "location_text": item.get("location_text", ""),
            "district": item.get("district", ""),
        })

    _RE_RESERVED_TITLE = re.compile(r"\s*rezervov[aá]no\s*$", re.IGNORECASE)

    @classmethod
    def apply_list_item(cls, normalized: Dict[str, Any], item: Dict[str, Any]) -> None:
        """Do dat z detailu doplní lokalitu z výpisu (obec, okres) a štítek rezervace z titulku."""
        normalized["location_text"] = item["location_text"][:200]
        normalized["municipality"] = item["municipality"]
        normalized["district"] = item["district"]

        title = normalized.get("title") or ""
        if cls._RE_RESERVED_TITLE.search(title):
            # iDNES lepí „rezervováno" hned za titulek („Prodej pole 50 076 m²rezervováno")
            normalized["title"] = cls._RE_RESERVED_TITLE.sub("", title)
            normalized["price_note"] = "Rezervace"
            normalized["keep_last_price"] = True

    @http_retry
    async def _fetch_page(self, url: str) -> str:
        """Fetch detail page via HTTP. Opakuje při 429/503."""
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")

        logger.debug(f"Fetching: {url}")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    def _parse_detail_page(self, html: str, url: str) -> Optional[Dict[str, Any]]:
        """
        Parse detail page HTML.

        Extracts:
        - Title
        - Price
        - Location
        - Property type
        - Offer type
        - Photos
        - Description
        - Area (if available)
        """
        soup = BeautifulSoup(html, "html.parser")

        try:
            # Extract title
            title_elem = soup.find("h1", class_=re.compile("title|heading|main-title"))
            if not title_elem:
                title_elem = soup.select_one("h1")
            title = title_elem.get_text(strip=True) if title_elem else "N/A"

            # Extract price - IDNES uses .b-detail__price
            price = None
            for sel in [".b-detail__price", ".cena", "[itemprop='price']"]:
                price_elem = soup.select_one(sel)
                if price_elem:
                    price_text = price_elem.get_text(strip=True)
                    # IDNES wraps digits with ZWJ (\u200d) and NBSP (\u00a0) – strip them first
                    price_text = price_text.replace("\u200d", "").replace("\u00a0", " ")
                    # Match a plausible Czech price: 4-9 digits optionally separated by spaces/dots
                    # e.g. "1 500 000 Kč" or "2.500.000 Kč" or "950000 Kč"
                    price_match = re.search(r"\b(\d[\d\s.]{2,10}\d)\s*(Kč|CZK)", price_text)
                    if price_match:
                        digits = re.sub(r"[^\d]", "", price_match.group(1))
                        try:
                            val = int(digits)
                            # Sanity check: 10 000 – 500 000 000 Kč
                            if 10_000 <= val <= 500_000_000:
                                price = val
                        except ValueError:
                            pass
                    break

            # Extract location - try HTML first, fallback to URL slug
            # IDNES uses .b-detail__info-item or address elements
            location = None
            for sel in [
                ".b-detail__info .icoi-location",
                ".b-detail__info-item--location",
                "[itemprop='addressLocality']",
                ".b-detail__place",
            ]:
                elem = soup.select_one(sel)
                if elem:
                    location = elem.get_text(strip=True)
                    break

            # Fallback: extract location slug from URL path
            # URL format: /detail/{prodej|pronajem}/{type}/{location-slug}/{id}/
            if not location:
                url_parts = url.rstrip("/").split("/")
                # Parts: ['', 'detail', 'prodej', 'dum', 'znojmo-na-valech', 'ID']
                if len(url_parts) >= 5:
                    location_slug = url_parts[-2]
                    # Convert kebab-case slug to readable text
                    location = location_slug.replace("-", " ").title()

            location = location or "Znojmo"

            property_type, url_offer_type = self.types_from_url(url)
            url_lower = url.lower()

            # Offer type from URL
            offer_type = url_offer_type

            # Extract photos - IDNES: plain <img> without class, src from sta-reality2.1gr.cz
            photos = []
            for img in soup.find_all("img"):
                src = img.get("src") or img.get("data-src") or img.get("data-lazy") or ""
                # Photos are served from sta-reality2.1gr.cz or iDnes CDN
                if ("1gr.cz/sta/compile" in src or "gallery" in src.lower() or "photo" in src.lower()
                        or ("idnes" in src.lower() and "/sta/" in src)):
                    if src.startswith("//"):
                        src = "https:" + src
                    if src.startswith("http") and src not in photos:
                        photos.append(src)
            # Fallback: og:image
            if not photos:
                for og in soup.find_all("meta", property="og:image"):
                    content = og.get("content")
                    if content:
                        photos.append(content)
            photos = list(dict.fromkeys(photos))[:50]  # deduplicate, max 50

            # Extract description - IDNES uses different selectors per property type:
            # Residential: .b-detail__desc / .b-detail__text
            # Commercial/Other: .b-desc (different BEM variant)
            # Fallback 1: og:description meta tag (reliable, always 150-300 chars)
            # Fallback 2: long <p> paragraph (last resort, skip SEO navigation text)
            description = ""
            for sel in [".b-detail__desc", ".b-detail__text", ".b-desc", "[itemprop='description']"]:
                elem = soup.select_one(sel)
                if elem:
                    description = elem.get_text(strip=True)
                    break
            # Fallback 1: og:description / meta description (reliable summary)
            if not description:
                meta = soup.find("meta", attrs={"property": "og:description"}) or \
                       soup.find("meta", attrs={"name": "description"})
                if meta:
                    content = meta.get("content", "")
                    if len(content) > 20:
                        description = content
            # Fallback 2: long <p> paragraph – skip SEO navigation text (repetitive patterns)
            if not description:
                for p in soup.select("p"):
                    t = p.get_text(" ", strip=True)
                    # 100–2000 chars, not navigation/legal text
                    if (100 < len(t) < 2000
                            and "cookie" not in t.lower()
                            and "mafra" not in t.lower()
                            and "©" not in t
                            and "@" not in t
                            and t.lower().count("pronájem") < 4
                            and t.lower().count("prodej") < 4):
                        description = t
                        break

            # Plochy z titulku – IDNES má jednotný formát "Prodej domu 135 m² s pozemkem 212 m²".
            # Dřív se brala jen první "(\d+) m²": "1 809 m²" → 809, "50 076 m²" → 76
            # a pozemek se nikam neukládal (detekce duplikátů pak neměla co porovnat).
            area, area_land = parse_title_areas(title)
            if property_type == "Land" and area and not area_land:
                area_land, area = area, None  # "Prodej zahrady 1 809 m²" = výměra pozemku
            # Fallback: tabulka parametrů (.b-detail__info)
            if not area and not area_land:
                for row in soup.select(".b-detail__info-item, .b-detail__param"):
                    text = row.get_text(" ", strip=True)
                    area_match = re.search(r"Plocha\D+?(\d+)\s*m", text, re.IGNORECASE)
                    if area_match:
                        area = int(area_match.group(1))
                        break

            # Return normalized data
            return {
                "source_code": self.SOURCE_CODE,
                "external_id": self._extract_external_id(url),
                "url": url,
                "title": title[:200],
                "description": description[:5000],
                "property_type": property_type,
                "offer_type": offer_type,
                "price": price,
                "location_text": location[:200] if location else "Znojmo",
                "photos": photos[:50],
                "area_built_up": area,
                "area_land": area_land,
            }

        except Exception as exc:
            logger.error(f"Error parsing detail page {url}: {exc}")
            return None

    @staticmethod
    def _extract_external_id(url: str) -> str:
        """
        Extract external ID from URL.

        URL format: https://reality.idnes.cz/detail/prodej/dum/znojmo/68f114793da2f02fc20a2b19/
        ID is last path segment (hexadecimal or numeric).
        """
        # Strip trailing slash, take last path segment
        return url.rstrip("/").split("/")[-1]

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        """Save listing to database."""
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info(
            "Saved listing %s: %s | %s Kč",
            listing_id,
            listing.get("title", "N/A")[:50],
            listing.get("price", "N/A"),
        )
