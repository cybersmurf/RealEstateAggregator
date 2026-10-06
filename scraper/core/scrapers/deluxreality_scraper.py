"""
DeluXreality scraper – deluxreality.cz
Realitní kancelář Znojmo (Delux services s.r.o.)
WordPress / Elementor SSR – httpx + BeautifulSoup
"""
import logging
import re
from typing import Any, Dict, List, Optional
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup

from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://deluxreality.cz"
LISTING_PAGES = [
    f"{BASE_URL}/nemovitosti/?typ=prodej",
    f"{BASE_URL}/nemovitosti/?typ=pronajem",
]

HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/122.0 Safari/537.36"
    ),
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Accept": "text/html,application/xhtml+xml,*/*;q=0.8",
}

OFFER_TYPE_MAP = {
    "prodej": "Sale",
    "pronájem": "Rent",
    "pronajem": "Rent",
}

# Typ nemovitosti podle slov v popisku karty („Rodinný dům · 4+1 · 137 m²") nebo v názvu.
# Rozhoduje slovo, které je v textu nejdřív: „Stavební pozemek pro rodinný dům" je pozemek,
# „Prodej bytu 4+kk s garáží" byt. „RD" = rodinný dům („Novostavba RD 4+kk Šatov").
PROPERTY_TYPE_PATTERNS = [
    (re.compile(r"\bbyt|apartm|garson"), "Apartment"),
    (re.compile(r"\brd\b|rodinn|\bd[ůu]m\b|\bdomu\b|\bdum\b|\bvil[ay]\b|řadov"), "House"),
    (re.compile(r"\bchat[ay]?\b|chalup|rekrea|horsk"), "Cottage"),
    (re.compile(r"pozem|parcel|zahrad|\bpole\b|\bles\b|\blouk|vinic"), "Land"),
    (re.compile(r"komer[čc]|obchod|kancel|sklad|prostor|restaur|hotel|pen[sz]ion"), "Commercial"),
    (re.compile(r"gar[áa][žz]"), "Garage"),
]

# Nabídky v cizině (realitka prodává i investiční apartmány v Polsku) nesmí dostat okres Znojmo
FOREIGN_RE = re.compile(
    r"polsk|rakousk|slovensk|maďarsk|chorvatsk|španělsk|itáli|bulharsk|dubaj|egypt|thajsk|kypr", re.I
)

# Okres jen tam, kde ho web výslovně uvádí: adresa „Šumná, okres Znojmo", popis „… okres Znojmo".
_DISTRICT_STATED = [
    (re.compile(r"okres\s+znojmo", re.I), "Znojmo"),
    (re.compile(r"okres\s+brno[\s-]*venkov", re.I), "Brno-venkov"),
    (re.compile(r"okres\s+brno[\s-]*m[ěe]sto", re.I), "Brno-město"),
]
# Slabší vodítko jen pro adresu bez obce („Šatovská 9"): popis „… ve Znojmě". U nabídky s obcí
# se nepoužije – „20 minut od centra ve Znojmě" by vesnici z jiného okresu přiřadilo ke Znojmu.
_CITY_STATED = [
    (re.compile(r"\bve\s+znojmě", re.I), "Znojmo"),
    (re.compile(r"\bv\s+brně", re.I), "Brno-město"),
]


class DeluxRealityScraper:
    SOURCE_CODE = "DELUXREALITY"

    async def run(self, full_rescan: bool = False) -> int:
        """Run the scraper and return count of processed listings."""
        logger.info(f"[{self.SOURCE_CODE}] Starting scrape (full_rescan={full_rescan})")
        try:
            return await self.scrape()
        except Exception as e:
            logger.error(f"[{self.SOURCE_CODE}] Fatal error: {e}", exc_info=True)
            return 0

    async def scrape(self) -> int:
        """Fetch listing page, then detail pages, persist to DB."""
        async with httpx.AsyncClient(headers=HEADERS, follow_redirects=True, timeout=30) as client:
            items = await self._get_listing_items(client)
            logger.info(f"[{self.SOURCE_CODE}] Found {len(items)} listings")

            count = 0
            db = get_db_manager()
            for list_item in items:
                url = list_item["url"]
                try:
                    item = await self._parse_detail(client, list_item)
                    if item:
                        if item.pop("sold", False):
                            # Prodaná / pronajatá nabídka, kterou web ještě ukazuje – u nás aktivní být nesmí
                            await db.deactivate_listing(self.SOURCE_CODE, item["external_id"])
                            count += 1
                            continue
                        await db.upsert_listing(item)
                        count += 1
                        logger.debug(f"[{self.SOURCE_CODE}] Saved: {item.get('title','?')}")
                except Exception as e:
                    logger.warning(f"[{self.SOURCE_CODE}] Error parsing {url}: {e}")

        logger.info(f"[{self.SOURCE_CODE}] Done – {count} listings saved")
        return count

    # ------------------------------------------------------------------
    # Private helpers
    # ------------------------------------------------------------------

    @http_retry
    async def _fetch(self, client: httpx.AsyncClient, url: str) -> str:
        """Stahne stránku, při 429/503 automaticky opakuje (max 3×)."""
        resp = await client.get(url)
        resp.raise_for_status()
        return resp.text

    async def _get_listing_items(self, client: httpx.AsyncClient) -> List[Dict[str, Any]]:
        """Scrape prodej+pronajem filter pages (with pagination) and return unique listing cards."""
        items: List[Dict[str, Any]] = []
        seen: set = set()

        for filter_url in LISTING_PAGES:
            # Typ nabídky určuje filtr, ze kterého karta pochází – název to říct nemusí
            # („Byt 2+kk ulice Šatovská 9" je pronájem a ukládal se jako prodej za 11 500 Kč).
            offer_type = "Rent" if "typ=pronajem" in filter_url else "Sale"
            page = 1
            while page <= 10:  # safety cap
                url = f"{filter_url}&paged={page}" if page > 1 else filter_url
                try:
                    html = await self._fetch(client, url)
                except httpx.HTTPStatusError as e:
                    if e.response.status_code == 404:
                        break  # no more pages
                    raise

                new_items = [it for it in self.parse_list_page(html, offer_type) if it["url"] not in seen]
                if not new_items:
                    break  # no more pages for this filter
                for it in new_items:
                    seen.add(it["url"])
                items.extend(new_items)
                page += 1

        return items

    @staticmethod
    def parse_list_page(html: str, offer_type: str) -> List[Dict[str, Any]]:
        """Karty výpisu (.dx-card): URL, název, adresa, cena, popisek typu a štítek („REZERVOVÁNO")."""
        soup = BeautifulSoup(html, "html.parser")
        items: List[Dict[str, Any]] = []
        seen: set = set()

        def _is_detail(href: str) -> bool:
            return bool(href) and "/nemovitosti/" in href and not re.search(
                r"/nemovitosti/\?|/nemovitosti/$|/feed/|/nemovitosti/page/", href
            )

        for card in soup.select(".dx-card"):
            link = next((a for a in card.select("a[href]") if _is_detail(a.get("href", "").strip())), None)
            href = link.get("href", "").strip() if link else ""
            if not href:
                # Karta je klikací přes onclick="window.location='…'"
                m = re.search(r"window\.location\s*=\s*'([^']+)'", card.get("onclick", "") or "")
                href = m.group(1) if m else ""
            if not _is_detail(href):
                continue
            full_url = urljoin(BASE_URL, href)
            if full_url in seen:
                continue
            seen.add(full_url)

            def _text(selector: str) -> str:
                el = card.select_one(selector)
                return el.get_text(" ", strip=True) if el else ""

            meta_el = card.select_one(".dx2-meta")
            meta_parts = [part.strip() for part in meta_el.get_text("·", strip=True).split("·")] if meta_el else []
            items.append({
                "url": full_url,
                "offer_type": offer_type,
                "title": _text(".dx2-sr"),
                "address": _text(".dx2-addr"),
                "price_text": _text(".dx2-price"),
                "meta": [part for part in meta_parts if part],
                "badge": _text(".dx-badge"),
            })

        if items:
            return items

        # Záloha při změně šablony: aspoň odkazy na detaily
        for a in soup.select("a[href*='/nemovitosti/']"):
            href = a.get("href", "").strip()
            if not _is_detail(href):
                continue
            full_url = urljoin(BASE_URL, href)
            if full_url not in seen:
                seen.add(full_url)
                items.append({"url": full_url, "offer_type": offer_type})
        return items

    async def _parse_detail(
        self, client: httpx.AsyncClient, list_item: Dict[str, Any]
    ) -> Optional[Dict[str, Any]]:
        """Fetch and parse a single property detail page."""
        html = await self._fetch(client, list_item["url"])
        return self.parse_detail_page(html, list_item)

    def parse_detail_page(self, html: str, list_item: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        """Detail nabídky. Cena, stav a adresa se berou z postranního panelu (.dx-attr-*),
        záložně z karty výpisu – ne z libovolného textu stránky."""
        url = list_item["url"]
        soup = BeautifulSoup(html, "html.parser")

        # External ID = URL slug after /nemovitosti/
        slug_match = re.search(r"/nemovitosti/([^/]+)/?$", url)
        external_id = slug_match.group(1) if slug_match else url

        # Title
        h1 = soup.find("h1")
        title = h1.get_text(strip=True) if h1 else (list_item.get("title") or "")
        if not title:
            logger.warning(f"[{self.SOURCE_CODE}] No title at {url}")
            return None

        attrs = self._sidebar_attrs(soup)

        # Offer type – filtr výpisu (typ=prodej / typ=pronajem), záložně název
        offer_type = list_item.get("offer_type") or self._detect_offer_type(soup, title)

        # Property type – popisek karty, pak název
        meta = list_item.get("meta") or []
        property_type = self._detect_property_type(title, meta[0] if meta else "")

        # Price
        price_text = attrs.get("price") or list_item.get("price_text") or ""
        price = self._parse_price_text(price_text)
        if price is None and not price_text:
            price = self._extract_price(soup, min_amount=1000 if offer_type == "Rent" else 10000)

        # Area – u pozemku je „Plocha" výměra pozemku
        area = self._parse_area_text(attrs.get("plocha", "")) or self._extract_area(soup)

        # Location
        description = self._extract_description(soup)
        location = (attrs.get("lokalita") or list_item.get("address") or "").strip()
        municipality, district = self._parse_location(location, title, description)

        # Photos – empty <a> tags linking to full-size images in wp-content/uploads
        photos = self._extract_photos(soup, url)

        item: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": external_id,
            "url": url,
            "title": title,
            "description": description,
            "price": price,
            "offer_type": offer_type,
            "property_type": property_type,
            "area_built_up": None if property_type == "Land" else area,
            "location_text": location[:200],
            "photos": photos,
        }
        if property_type == "Land" and area:
            item["area_land"] = area
        if municipality:
            item["municipality"] = municipality[:100]
        if district:
            item["district"] = district

        # Stav nabídky: postranní panel („aktivní" / „rezervováno"), štítek karty („REZERVOVÁNO")
        status = f'{attrs.get("status", "")} {list_item.get("badge", "")}'.lower()
        if re.search(r"prodán|pronaj", status):
            item["sold"] = True
        elif "rezerv" in status:
            # Rezervace občas padne – nabídka zůstává vidět, se štítkem a poslední známou cenou
            item["price_note"] = "Rezervace"
            item["keep_last_price"] = True
        elif price is None and price_text:
            item["price_note"] = price_text[:200]  # „Cena na dotaz"
        return item

    # ------------------------------------------------------------------
    # Field extractors
    # ------------------------------------------------------------------

    @staticmethod
    def _sidebar_attrs(soup: BeautifulSoup) -> Dict[str, str]:
        """Postranní panel detailu: cena, stav a mřížka hodnot (Dispozice, Plocha, Lokalita)."""
        attrs: Dict[str, str] = {}
        price_el = soup.select_one(".dx-attr-price")
        if price_el:
            attrs["price"] = price_el.get_text(" ", strip=True)
        status_el = soup.select_one(".dx-attr-status")
        if status_el:
            attrs["status"] = status_el.get_text(" ", strip=True)
        for cell in soup.select(".dx-attr-cell"):
            label = cell.select_one(".dx-attr-lbl")
            value = cell.select_one(".dx-attr-val")
            if label and value:
                attrs[label.get_text(" ", strip=True).lower()] = value.get_text(" ", strip=True)
        return attrs

    @staticmethod
    def _parse_price_text(text: str) -> Optional[float]:
        """„6 500 000 Kč" → 6500000, „9 000 Kč /měs." → 9000; text bez částky („Cena na dotaz") → None."""
        match = re.search(r"\d[\d\s\xa0.]*", text or "")
        if not match:
            return None
        digits = re.sub(r"[^\d]", "", match.group(0))
        return float(int(digits)) if digits and int(digits) > 0 else None

    @staticmethod
    def _parse_area_text(text: str) -> Optional[float]:
        match = re.search(r"(\d[\d\s\xa0]*(?:[.,]\d+)?)\s*m", text or "")
        if not match:
            return None
        try:
            return float(re.sub(r"[\s\xa0]", "", match.group(1)).replace(",", "."))
        except ValueError:
            return None

    @staticmethod
    def _parse_location(location: str, title: str, description: str) -> tuple[Optional[str], Optional[str]]:
        """(obec, okres) z adresy v postranním panelu.

        Dřív se lokalita hledala jako první text se slovem „Znojmo" kdekoli na stránce – tím byl
        titulek „… – DELUX Znojmo", takže okres Znojmo dostaly i investiční apartmány v Polsku.
        Okres se vyplní jen tam, kde ho web uvádí; zbytek doplní obecné určení okresu podle obce.
        """
        if FOREIGN_RE.search(f"{location} {title}"):
            return None, None

        parts = [part.strip() for part in location.split(",") if part.strip()]
        district: Optional[str] = None
        municipality: Optional[str] = None
        for pattern, name in _DISTRICT_STATED:
            if pattern.search(location):
                district = name
                break
        parts = [part for part in parts if not part.lower().startswith("okres")]
        if parts:
            # „Šatovská 495/9, Znojmo - Oblekovice" → Znojmo; samotná „Šatovská 9" obec není
            candidate = parts[-1].split(" - ")[0].strip()
            if not re.search(r"\d", candidate):
                municipality = candidate
        if municipality and not district:
            if municipality.lower().startswith("znojmo"):
                district = "Znojmo"
            elif municipality.lower().startswith("brno"):
                district = "Brno-město"
        if not district:
            patterns = _DISTRICT_STATED if municipality else _DISTRICT_STATED + _CITY_STATED
            for pattern, name in patterns:
                if pattern.search(description[:1500]):
                    district = name
                    break
        return municipality, district

    def _detect_offer_type(self, soup: BeautifulSoup, title: str) -> str:
        text = (title + " " + (soup.find("h2").get_text(" ", strip=True) if soup.find("h2") else "")).lower()
        for keyword, otype in OFFER_TYPE_MAP.items():
            if keyword in text:
                return otype
        return "Sale"

    @staticmethod
    def _detect_property_type(title: str, card_label: str = "") -> str:
        """Typ z popisku karty („Rodinný dům"), jinak z názvu – vyhrává slovo, které stojí nejdřív."""
        for text in (card_label, title):
            text = (text or "").lower()
            best: Optional[tuple[int, str]] = None
            for pattern, ptype in PROPERTY_TYPE_PATTERNS:
                match = pattern.search(text)
                if match and (best is None or match.start() < best[0]):
                    best = (match.start(), ptype)
            if best:
                return best[1]
        return "Other"

    def _extract_price(self, soup: BeautifulSoup, min_amount: int = 10000) -> Optional[float]:
        """Záloha bez postranního panelu: první samostatná částka v Kč na stránce."""
        for el in soup.find_all(string=re.compile(r"\d[\d\s]+Kč")):
            raw = el.strip()
            # Skip long strings (descriptions), grab clean price strings
            if len(raw) > 60:
                continue
            # Parse digits
            nums = re.sub(r"[^\d]", "", raw)
            if nums and int(nums) > min_amount:
                return float(int(nums))
        return None

    def _extract_area(self, soup: BeautifulSoup) -> Optional[float]:
        """Extract usable area in m²."""
        # Strategy 1: look for "Plocha" header followed by numeric value
        for heading in soup.find_all(re.compile(r"^h\d$"), string=re.compile(r"Plocha", re.I)):
            nxt = heading.find_next(string=re.compile(r"\d+\s*m"))
            if nxt:
                m = re.search(r"(\d+(?:[.,]\d+)?)", nxt)
                if m:
                    return float(m.group(1).replace(",", "."))

        # Strategy 2: bullet list item "plocha bytu: XX m²"
        for el in soup.find_all(string=re.compile(r"plocha\s+bytu|užitná\s+plocha|plocha\s+domu", re.I)):
            m = re.search(r"(\d+(?:[.,]\d+)?)\s*m", el, re.I)
            if m:
                return float(m.group(1).replace(",", "."))

        # Strategy 3: any text matching standalone "XX m²" pattern
        for el in soup.find_all(string=re.compile(r"\b\d{2,4}\s*m[²2]")):
            m = re.search(r"(\d+(?:[.,]\d+)?)\s*m[²2]", el)
            if m:
                val = float(m.group(1).replace(",", "."))
                if 10 < val < 2000:
                    return val
        return None

    def _extract_description(self, soup: BeautifulSoup) -> str:
        """Get the main property description text."""
        # Jen obsah nabídky (main.dx-ps-content) – bez něj by se do popisu míchaly texty patičky
        container = soup.select_one("main.dx-ps-content, .dx-ps-content") or soup
        paragraphs = []
        for p in container.select("p"):
            txt = p.get_text(" ", strip=True)
            if len(txt) > 80:
                paragraphs.append(txt)
        return "\n\n".join(paragraphs[:6]) if paragraphs else ""

    def _extract_photos(self, soup: BeautifulSoup, base_url: str) -> List[str]:
        """Extract full-size photo URLs from .dx-ps-gallery imgs (srcset largest width)."""
        photos = []
        seen = set()
        for img in soup.select(".dx-ps-gallery img"):
            full_url = None
            # Prefer srcset: parse and take the largest width entry
            srcset = img.get("srcset", "")
            if srcset:
                best_w = 0
                for entry in srcset.split(","):
                    parts = entry.strip().rsplit(" ", 1)
                    if len(parts) == 2:
                        try:
                            w = int(parts[1].rstrip("w"))
                            if w > best_w:
                                best_w = w
                                full_url = parts[0].strip()
                        except ValueError:
                            pass
            # Fallback: strip WordPress size suffix from src (e.g. -300x169)
            if not full_url:
                src = img.get("src", "")
                if src:
                    full_url = re.sub(r"-\d+x\d+(\.\w+)$", r"\1", src)
            if not full_url:
                continue
            if not full_url.startswith("http"):
                full_url = urljoin(BASE_URL, full_url)
            if full_url not in seen:
                seen.add(full_url)
                photos.append(full_url)
            if len(photos) >= 20:
                break
        return photos
