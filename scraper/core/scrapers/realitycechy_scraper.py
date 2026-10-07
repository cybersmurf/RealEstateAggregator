"""
RealityCechy.cz scraper – celostátní inzertní portál (Nette, SSR HTML).

Strategie: httpx + BeautifulSoup. Výpis /nemovitosti/prodej-domu/ (resp.
prodej-bytu/, prodej-pozemku/) umí filtrovat po okresech přes query parametr
`okresy_id[0]=<id>` (ID = kraj 2002 + kód okresu: Znojmo 20023713,
Brno-venkov 20023703, Brno-město 20023702; zjištěno z odkazu
„/hledat/nabidky?okresy_id…“ na stránce /nemovitosti/okres/<slug>).
Stránkování `?vp-page=N`, 24 karet `div.nem-item[id]` na stránku, celkový
počet v `.pocet-inzeratu .cislo`. Ve výpisu je i ld+json ItemList (záloha).

Detail: h1.entry-title, h2.mesto-ulice, tabulky „Adresa“ (Obec/Okres/Kraj)
a „Informace“ (Cena, Stav, Druh, Typ, Zastavěná plocha, Výměra pozemku,
Užitná/Obytná plocha, Postaveno z), popis p.realita-popis, galerie
li[data-fancybox] data-src=/foto/hq/…webp (plná velikost, 200), GPS ve
skriptu `var x = lat; var y = lon;` (bývá střed obce). ld+json
RealEstateListing má jen zkrácený popis a mq fotky – používáme jako zálohu.

Proč: 30. 9. 2026 hlásil RealityCechy 871 rodinných domů v JMK (170 domů,
74 bytů a 143 pozemků v okrese Znojmo); inzerují sem i menší RK bez Sreality.
robots.txt povoluje /nemovitosti/ i /nemovitost/ pro běžné klienty
(blokuje jen tréninkové AI boty podle User-Agentu).
"""
import asyncio
import json
import logging
import re
import time
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup, Tag

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://www.realitycechy.cz"

# okresy_id[0] = <kraj><okres>; JMK = 2002
DISTRICT_IDS: Dict[str, int] = {
    "Znojmo": 20023713,
    "Brno-venkov": 20023703,
    "Brno-město": 20023702,
    "Břeclav": 20023704,   # 7. 10. 2026 – jen obce z partial_districts, zbytek zahodí filtr
}

# (cesta výpisu, typ nemovitosti, typ nabídky)
TYPE_PATHS: List[Tuple[str, str, str]] = [
    ("/nemovitosti/prodej-domu/", "Dům", "Prodej"),
    ("/nemovitosti/prodej-bytu/", "Byt", "Prodej"),
    ("/nemovitosti/prodej-pozemku/", "Pozemek", "Prodej"),
]

# (cesta výpisu, typ nemovitosti, typ nabídky, okres, okresy_id)
DEFAULT_LISTS: List[Tuple[str, str, str, str, int]] = [
    (path, ptype, otype, district, okres_id)
    for district, okres_id in DISTRICT_IDS.items()
    for path, ptype, otype in TYPE_PATHS
]

PAGE_SIZE = 24

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": BASE_URL,
}

OFFER_TYPE_MAP = {"prodej": "Prodej", "pronájem": "Pronájem", "pronajem": "Pronájem", "dražba": "Dražba", "drazba": "Dražba"}

# Druh z detailu → typ nemovitosti (podřetězce, pořadí = priorita)
KIND_MAP: List[Tuple[str, str]] = [
    ("chat", "Chata"), ("chalup", "Chata"),
    ("rodinný dům", "Dům"), ("rodinny dum", "Dům"), ("vila", "Dům"), ("usedlost", "Dům"), ("dvojdům", "Dům"),
    ("byt", "Byt"),
    ("komerč", "Komerční"), ("kancel", "Komerční"), ("obchod", "Komerční"), ("sklad", "Komerční"), ("výrob", "Komerční"),
    ("stavební", "Pozemek"), ("zahrad", "Pozemek"), ("pole", "Pozemek"), ("louk", "Pozemek"), ("les", "Pozemek"),
    ("vinic", "Pozemek"), ("pozem", "Pozemek"), ("ostatní", "Pozemek"),
]

DISPOSITION_RE = re.compile(r"(\d)\s*\+\s*(kk|\d)", re.I)
GPS_RE = re.compile(r"var x = ([\d.]+);\s*var y = ([\d.]+);")
UNSET_VALUES = {"", "neuvedeno", "neuvedena", "nezadána", "nezadáno", "-"}


class RealityCechyScraper:
    """Scraper pro realitycechy.cz (SSR HTML, filtr po okresech)."""

    SOURCE_CODE = "REALITYCECHY"

    def __init__(self, lists: Optional[List[Tuple[str, str, str, str, int]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 20 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting RealityCechy scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                for path, property_type, offer_type, district, okres_id in self.lists:
                    await self._scrape_list(path, property_type, offer_type, district, okres_id, max_pages, metrics)
        self._http_client = None
        logger.info("RealityCechy scraper done. Scraped %s", self.scraped_count)
        return self.scraped_count

    @staticmethod
    def build_list_url(path: str, okres_id: int, page: int = 1) -> str:
        query = f"okresy_id%5B0%5D={okres_id}"
        if page > 1:
            query = f"vp-page={page}&{query}"
        return f"{urljoin(BASE_URL, path)}?{query}"

    async def _scrape_list(self, path: str, property_type: str, offer_type: str, district: str,
                           okres_id: int, max_pages: int, metrics: Any) -> None:
        page = 1
        while page <= max_pages:
            url = self.build_list_url(path, okres_id, page)
            try:
                with timer(f"Fetch list {path} {district} page {page}"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
                items, total, has_next = self.parse_list_page(html, page)
                if not items:
                    logger.info("No items on %s (%s) page %s, stopping", path, district, page)
                    break
                logger.info("%s %s page %s: %s listings (total %s)", path, district, page, len(items), total)
                for item in items:
                    try:
                        detail_html = await self._fetch(item["url"])
                        normalized = self.parse_detail_page(detail_html, item, property_type, offer_type, district)
                        await self._save_listing(normalized)
                        self.scraped_count += 1
                        metrics.increment_scraped()
                        await asyncio.sleep(0.4)
                    except Exception as exc:
                        logger.error("Error processing %s: %s", item.get("url"), exc)
                        metrics.increment_failed()
                if not has_next or (total and page * PAGE_SIZE >= total):
                    break
                page += 1
                await asyncio.sleep(1.0)
            except httpx.HTTPStatusError as exc:
                logger.error("HTTP error %s page %s: %s", path, page, exc)
                break
            except Exception as exc:
                logger.error("Error %s page %s: %s", path, page, exc)
                metrics.increment_failed()
                break

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── parsování (čisté metody, testovatelné na uloženém HTML) ──────────────

    @staticmethod
    def _ld_json(soup: BeautifulSoup) -> List[Dict[str, Any]]:
        """Všechny objekty z @graph ld+json skriptů (Organization, ItemList, RealEstateListing…)."""
        objects: List[Dict[str, Any]] = []
        for script in soup.find_all("script", type="application/ld+json"):
            try:
                data = json.loads(script.string or "")
            except (ValueError, TypeError):
                continue
            graph = data.get("@graph") if isinstance(data, dict) else None
            if isinstance(graph, list):
                objects.extend(o for o in graph if isinstance(o, dict))
            elif isinstance(data, dict):
                objects.append(data)
        return objects

    @staticmethod
    def _parse_number(text: Optional[str]) -> Optional[float]:
        if not text:
            return None
        m = re.search(r"(\d[\d\s\xa0]*(?:[.,]\d+)?)", text)
        if not m:
            return None
        raw = m.group(1).replace(" ", "").replace("\xa0", "").replace(",", ".")
        try:
            return float(raw)
        except ValueError:
            return None

    @staticmethod
    def _text_with_sup(el: Tag) -> str:
        """Text elementu, kde „2 336 m<sup>2</sup>“ zůstane „2 336 m2“ (ne „m 2“)."""
        for sup in el.find_all("sup"):
            sup.replace_with(sup.get_text(strip=True))
        return re.sub(r"\s+", " ", el.get_text("")).strip()

    @staticmethod
    def _clean_municipality(text: str) -> str:
        """„ulice Čechova, Břeclav“ → „Břeclav“; „Sebranice“ → „Sebranice“."""
        text = re.sub(r"\s+", " ", text).strip()
        if text.lower().startswith("ulice ") and "," in text:
            text = text.split(",")[-1]
        return text.strip()

    def parse_list_page(self, html: str, page: int = 1) -> Tuple[List[Dict[str, Any]], int, bool]:
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen: set = set()
        for card in soup.select("div.nem-item[id]"):
            external_id = (card.get("id") or "").strip()
            link = card.select_one(".nem-headline a[href]") or card.select_one("a[href*='/nemovitost/']")
            if not external_id.isdigit() or not link:
                continue
            if external_id in seen:
                continue
            seen.add(external_id)
            addr = card.select_one(".nem-ulice-mesto")
            price_el = card.select_one(".nem-cena-nemovitosti")
            status_el = card.select_one(".nem-novinka")
            thumb = card.select_one(".nem-gallery-photo img[src]")
            results.append({
                "external_id": external_id,
                "url": urljoin(BASE_URL, link.get("href", "")),
                "title": self._text_with_sup(link)[:200],
                "municipality": self._clean_municipality(addr.get_text(" ", strip=True))[:100] if addr else "",
                "price_text": price_el.get_text(" ", strip=True) if price_el else "",
                "status": status_el.get_text(" ", strip=True).lower() if status_el else "",
                "thumb": urljoin(BASE_URL, thumb["src"]) if thumb else "",
            })

        if not results:
            # záloha: ld+json ItemList (jen URL + název)
            for obj in self._ld_json(soup):
                if obj.get("@type") != "ItemList":
                    continue
                for el in obj.get("itemListElement") or []:
                    url = el.get("url") or ""
                    m = re.search(r"/(\d{5,})/?$", url)
                    if not m or m.group(1) in seen:
                        continue
                    seen.add(m.group(1))
                    results.append({"external_id": m.group(1), "url": url, "title": (el.get("name") or "")[:200],
                                    "municipality": "", "price_text": "", "status": "", "thumb": ""})

        total_el = soup.select_one(".pocet-inzeratu .cislo")
        total = int(self._parse_number(total_el.get_text()) or 0) if total_el else 0
        has_next = soup.select_one("a.paginator__next") is not None or any(
            f"vp-page={page + 1}" in (a.get("href") or "") for a in soup.select("a[href*='vp-page=']"))
        return results, total, has_next

    @staticmethod
    def _table_params(soup: BeautifulSoup) -> Dict[str, str]:
        """th→td z tabulek „Adresa“ a „Informace“; klíč lowercase bez dvojtečky."""
        params: Dict[str, str] = {}
        for tr in soup.select("table.detail_table_levy tr, table.detail_table_pravy tr"):
            th, td = tr.find("th"), tr.find("td")
            if not th or not td:
                continue
            key = th.get_text(" ", strip=True).rstrip(":").strip().lower()
            if key not in ("obec", "okres", "kraj"):
                # „spočítat hypotéku“ odkaz u ceny pryč (u adresy je v <a> samotná hodnota)
                for a in td.find_all("a"):
                    a.decompose()
            value = re.sub(r"\s+", " ", td.get_text(" ", strip=True)).strip()
            if key and key not in params:
                params[key] = value
        return params

    @staticmethod
    def _prehled(soup: BeautifulSoup) -> Dict[str, str]:
        """Rychlý přehled pod galerií (Vlastnictví, Druh, Výměra / Obytná plocha, Parkování)."""
        out: Dict[str, str] = {}
        for block in soup.select(".detail-prehled .detail_prehled_nadpis"):
            value = block.find_next_sibling("div", class_="detail_prehled_hodnota")
            if value:
                out[block.get_text(" ", strip=True).lower()] = re.sub(r"\s+", " ", value.get_text(" ", strip=True)).strip()
        return out

    @staticmethod
    def _normalize_district(text: str) -> str:
        """„Brno - venkov“ → „Brno-venkov“, „Brno - město“ → „Brno-město“."""
        text = re.sub(r"\s*-\s*", "-", text.strip())
        return text

    @staticmethod
    def _kind_to_property_type(kind: str, default: str) -> str:
        k = kind.lower()
        for needle, ptype in KIND_MAP:
            if needle in k:
                return ptype
        return default

    @staticmethod
    def _extract_seller(soup: BeautifulSoup, rk_name: str) -> Dict[str, Optional[str]]:
        """Makléř a e-mail z bloku „Realitní kancelář“; telefon portál vydává až po kliknutí (ajax), ten nečteme."""
        block = soup.select_one(".detail-nabizi")
        name_el = block.select_one("a[href*='/makler/']") if block else None
        emails = [a.get_text(" ", strip=True) for a in block.select("a.email")] if block else []
        emails = [e for e in emails if re.fullmatch(r"[^@\s]+@[^@\s]+\.[^@\s]+", e)]
        # e-mail makléře je v bloku až za e-mailem kanceláře
        email = emails[-1] if emails else None
        return {
            "seller_name": (re.sub(r"\s+", " ", name_el.get_text(" ", strip=True)) if name_el else "") or None,
            "seller_email": email,
            "seller_company": rk_name or None,
        }

    def parse_detail_page(self, html: str, item: Dict[str, Any], property_type: str, offer_type: str,
                          district: str) -> Dict[str, Any]:
        soup = BeautifulSoup(html, "html.parser")
        ld_listing = next((o for o in self._ld_json(soup) if o.get("@type") == "RealEstateListing"), {})

        h1 = soup.select_one("h1.entry-title") or soup.find("h1")
        title = self._text_with_sup(h1) if h1 else (ld_listing.get("name") or item.get("title") or "")
        title = re.sub(r"\s+", " ", title)[:200]

        params = self._table_params(soup)
        prehled = self._prehled(soup)

        # ── adresa ──
        obec = params.get("obec") or ""
        if obec.lower() in UNSET_VALUES:
            obec = ""
        if not obec:
            h2 = soup.select_one("h2.mesto-ulice")
            obec = h2.get_text(" ", strip=True).split(",")[0].strip() if h2 else ""
        obec = obec or item.get("municipality") or ""
        okres_raw = params.get("okres") or ""
        detail_district = self._normalize_district(okres_raw) if okres_raw and okres_raw.lower() not in UNSET_VALUES else ""
        district = detail_district or district
        street = params.get("ulice") or ""
        if street.lower() in UNSET_VALUES:
            street = ""
        location_parts = [p for p in (street, obec, f"okres {district}" if district else "") if p]
        location_text = ", ".join(location_parts) or f"okres {district}"

        # ── druh / typ nabídky ──
        kind = params.get("druh") or prehled.get("druh") or ""
        ptype = self._kind_to_property_type(kind, property_type)
        typ = (params.get("typ") or "").lower()
        otype = OFFER_TYPE_MAP.get(typ, offer_type)
        if otype == "Prodej" and ("dražb" in title.lower() or "aukc" in title.lower()):
            otype = "Dražba"

        # ── cena ──
        price = self._parse_number(params.get("cena", ""))
        if price is None:
            price = self._parse_number(item.get("price_text", ""))
        if price is None:
            ld_offer = ld_listing.get("offers") if isinstance(ld_listing.get("offers"), dict) else {}
            price = self._parse_number(str(ld_offer.get("price") or ""))

        # ── plochy ──
        area_land = self._parse_number(params.get("výměra pozemku") or params.get("plocha pozemku") or "")
        area_usable = self._parse_number(params.get("užitná plocha") or "")
        area_living = self._parse_number(params.get("obytná plocha") or "")
        area_built = self._parse_number(params.get("zastavěná plocha") or "")
        if ptype == "Pozemek":
            if area_land is None:
                area_land = self._parse_number(prehled.get("výměra") or "")
            area_built_up = None
        elif ptype == "Byt":
            area_built_up = area_usable or area_living or self._parse_number(prehled.get("obytná plocha") or "")
            area_land = None
        else:
            # „Výměra“ v rychlém přehledu je u domů výměra pozemku – jako plochu domu ji
            # bereme jen tehdy, když se od pozemku liší
            area_overview = self._parse_number(prehled.get("výměra") or "")
            if area_overview is not None and area_overview == area_land:
                area_overview = None
            # obytná plocha vypovídá o domě víc než zastavěná: ta je půdorys i s dvorem
            # a u řadových domů bývá stejná jako celý pozemek
            area_built_up = area_usable or area_living or area_built or area_overview

        # ── dispozice ──
        disposition = None
        rooms = None
        m = DISPOSITION_RE.search(kind) or DISPOSITION_RE.search(title)
        if m:
            disposition = f"{m.group(1)}+{m.group(2).lower()}"
            rooms = int(m.group(1))

        # ── popis ──
        desc_el = next((p for p in soup.select("p.realita-popis") if "mobile" not in (p.get("class") or [])), None) \
            or soup.select_one("p.realita-popis")
        if desc_el:
            for br in desc_el.find_all("br"):
                br.replace_with("\n")
            for sup in desc_el.find_all("sup"):
                sup.replace_with(sup.get_text())
            description = re.sub(r"[ \t]+", " ", desc_el.get_text()).strip()
            description = re.sub(r"\n\s*\n+", "\n\n", description)
        else:
            description = (ld_listing.get("description") or "").strip()
        extras: List[str] = []
        status = (params.get("status") or item.get("status") or "").strip().lower()
        if status and status not in UNSET_VALUES:
            extras.append(f"Status: {status}")
        # blok „Realitní kancelář“ (h4 > a /kancelar/…); .nem-rk-logo ve spodku stránky patří
        # souvisejícím inzerátům, ne tomuto
        rk = soup.select_one("h4 a[href*='/kancelar/']")
        rk_name = rk.get_text(" ", strip=True)[:100] if rk else ""
        if rk_name:
            extras.append(f"Realitní kancelář: {rk_name}")
        if extras:
            description = (description + "\n\n" + "\n".join(extras)).strip()

        # ── fotky ──
        photos: List[str] = []
        for li in soup.select("li[data-fancybox][data-src]"):
            src = li.get("data-src") or ""
            if "/foto/" not in src:
                continue
            full = urljoin(BASE_URL, src)
            if full not in photos:
                photos.append(full)
        if not photos:
            for src in ld_listing.get("image") or []:
                full = urljoin(BASE_URL, src)
                if full not in photos:
                    photos.append(full)
        # inzerát bez fotek má ve výpisu zástupný obrázek portálu (default_foto.jpg)
        if not photos and item.get("thumb") and "default_foto" not in item["thumb"]:
            photos.append(item["thumb"])

        # ── stav / konstrukce ──
        condition = params.get("stav") or ""
        condition = condition[:1].upper() + condition[1:] if condition.lower() not in UNSET_VALUES else None
        construction = params.get("postaveno z") or ""
        construction = construction[:1].upper() + construction[1:] if construction.lower() not in UNSET_VALUES else None

        external_id = item.get("external_id") or ""
        if not external_id:
            hid = soup.select_one("input#realita_id[value]")
            external_id = hid["value"] if hid else re.sub(r".*/(\d+)/?$", r"\1", item.get("url", ""))

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": str(external_id),
            "url": item.get("url") or ld_listing.get("url") or "",
            "title": title,
            "description": description[:5000],
            "property_type": ptype,
            "offer_type": otype,
            "price": price,
            "location_text": location_text[:200],
            "municipality": obec[:100] or None,
            "district": district or None,
            "area_built_up": area_built_up,
            "area_land": area_land,
            "rooms": rooms,
            "disposition": disposition,
            "condition": condition,
            "construction_type": construction,
            "photos": photos[:50],
        }
        result.update(self._extract_seller(soup, rk_name))
        gps = GPS_RE.search(html)
        if gps:
            lat, lon = float(gps.group(1)), float(gps.group(2))
            if 48.0 < lat < 51.5 and 12.0 < lon < 19.0:
                result["latitude"], result["longitude"] = lat, lon
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
