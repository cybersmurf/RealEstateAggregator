"""
OKdražby.cz scraper – dražební portál (OK dražby s.r.o., exekuční a veřejné
dražby nemovitostí; podle vlastních statistik největší dražebník v ČR 2025).

Strategie: httpx, SSR HTML Next.js app routeru. Stránka nemá `__NEXT_DATA__`,
ale RSC payload (`self.__next_f.push([1,"…"])`) obsahuje hotový JSON:
  • výpis  – jeden řádek na položku s `{"auction":{id,name,number,statusCode,
             region,county,start,finish,lowestSubmission,initialPrice,…}}`
             + `href="/drazba/<id>-<slug>"`,
  • detail – celý objekt `{"auction":{…}}` (adresa, GPS, kategorie, popis HTML,
             biddingMethodAttributes.lowestSubmission/estimatedPrice,
             auctionSecurity, imageSnippets, statusCode). Popis bývá odkaz
             `"$53"` na textový řádek `53:T<hexdélka>,<text>` – řešíme.
HTML neparsujeme vůbec; když RSC objekt chybí (změna webu), padáme na veřejné
JSON API `/api/v1/portal/auctions/<id>`, které vrací totéž.

Výpis: /drazby/nemovity/<domy|byty|pozemky>/<kraj-slug|okres-slug>, stránkování
`?page=N` po 15 položkách (odkazy `?page=N` v HTML). Web filtruje kraj/okres
cestou, takže stahujeme rovnou `/jihomoravsky-kraj` a pro jistotu ještě
kontrolujeme `county` proti seznamu okresů JMK. Přeskakujeme dražby, které
nejsou připravované/probíhající (vydražené, zrušené, odročené, bez podání…).

robots.txt (30. 9. 2026): Disallow jen /muj-ucet, /admin, /drazby/testovaci-drazby.

Proč: dražby jsou jediný způsob, jak koupit dům výrazně pod odhadní cenou;
Sreality dražby mají jen zlomek exekutorských úřadů. 30. 9. 2026 bylo v JMK
8 domů, 4 byty, 12 pozemků (celostátně 67 domů na 5 stránkách).
"""
import asyncio
import json
import logging
import re
import time
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import urljoin
from zoneinfo import ZoneInfo

import httpx
from bs4 import BeautifulSoup

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..http_utils import http_retry

logger = logging.getLogger(__name__)

BASE_URL = "https://okdrazby.cz"
API_URL = f"{BASE_URL}/api/v1/portal"
CDN_URL = "https://d1ws838f4e5d65.cloudfront.net/api/v1/portal"
PRAGUE = ZoneInfo("Europe/Prague")

# (cesta výpisu, výchozí typ nemovitosti) – kraj je součást cesty
DEFAULT_LISTS: List[Tuple[str, str]] = [
    ("/drazby/nemovity/domy/jihomoravsky-kraj", "Dům"),
    ("/drazby/nemovity/byty/jihomoravsky-kraj", "Byt"),
    ("/drazby/nemovity/pozemky/jihomoravsky-kraj", "Pozemek"),
]

# Okresy Jihomoravského kraje (hodnota `county` v datech webu)
JMK_DISTRICTS = frozenset({
    "Znojmo", "Brno-venkov", "Brno-město", "Břeclav", "Hodonín", "Vyškov", "Blansko",
})

# statusCode dražeb, které má smysl ukládat (ostatní = vydražená, zrušená, odročená, …)
ACTIVE_STATUSES = frozenset({"prepared", "ongoing", "priorityBidding"})

PAGE_SIZE = 15

DEFAULT_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    ),
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Referer": BASE_URL,
}

# Kód podkategorie (3. úroveň `categories`) → náš typ nemovitosti
SUBCATEGORY_MAP = {
    "family_house": "Dům", "villa": "Dům", "turnkey_house": "Dům", "farmstead": "Dům",
    "monument_others": "Dům", "chalet": "Chata", "cottage": "Chata",
    "lands_for_housing": "Pozemek", "gardens": "Pozemek", "fields": "Pozemek", "meadows": "Pozemek",
    "forests": "Pozemek", "other_lands": "Pozemek", "commercial_lots": "Pozemek", "ponds": "Pozemek",
    "tenement_house": "Komerční", "offices": "Komerční", "commercial_spaces": "Komerční",
    "restaurants": "Komerční", "warehouses": "Komerční", "accommodation": "Komerční",
    "production": "Komerční", "agricultural_object": "Komerční", "other_commercial_real_estate": "Komerční",
    "garage": "Garáž", "parking_place": "Garáž", "wine_cellar": "Ostatní", "attic": "Ostatní",
    "other_real_estate_sub": "Ostatní",
}
# Kategorie (2. úroveň) → typ, když podkategorie chybí
CATEGORY_MAP = {"houses": "Dům", "apartments": "Byt", "land": "Pozemek",
                "commercial_real_estate": "Komerční", "other_real_estate": "Ostatní"}

_RE_DISPOSITION_CODE = re.compile(r"^(\d)_(1|kk)$")
_RE_ZIP_CITY = re.compile(r"\b\d{3}\s?\d{2}\s+([^,]+)")
_RE_TITLE_MUNICIPALITY = re.compile(
    r"(?:v\s+obci|obec|v\s+k\.\s?ú\.|k\.\s?ú\.|v\s+kat(?:astrálním)?\.?\s+území)\s+"
    r"([A-ZÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ][^\s,()]+(?:\s+(?:u|nad|pod|v|na)\s+[^\s,()]+|\s+[A-ZÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ][^\s,()]+)*)"
)
_RE_AREA_M2 = re.compile(r"(\d{1,3}(?:[\s\xa0]\d{3})*|\d+)\s*m\s?(?:2|²)", re.IGNORECASE)


class OkdrazbyScraper:
    """Scraper pro okdrazby.cz (Next.js RSC payload + záložní JSON API)."""

    SOURCE_CODE = "OKDRAZBY"

    def __init__(self, lists: Optional[List[Tuple[str, str]]] = None,
                 districts: Optional[frozenset] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.districts = districts or JMK_DISTRICTS
        self.scraped_count = 0
        self.skipped_status = 0
        self.skipped_district = 0
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        max_pages = 10 if full_rescan else 3
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = 3) -> int:
        logger.info("Starting OKdražby scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                for path, property_type in self.lists:
                    await self._scrape_list(path, property_type, max_pages, metrics)
        self._http_client = None
        logger.info("OKdražby scraper done. Scraped %s, skipped status %s, skipped district %s",
                    self.scraped_count, self.skipped_status, self.skipped_district)
        return self.scraped_count

    async def _scrape_list(self, path: str, property_type: str, max_pages: int, metrics) -> None:
        page = 1
        while page <= max_pages:
            url = urljoin(BASE_URL, path if page == 1 else f"{path}?page={page}")
            try:
                with timer(f"Fetch list {path} page {page}"):
                    start = time.perf_counter()
                    html = await self._fetch(url)
                    metrics.record_fetch(time.perf_counter() - start)
                items, has_next = self.parse_list_page(html, page)
                if not items:
                    logger.info("No items on %s page %s, stopping", path, page)
                    break
                logger.info("%s page %s: %s auctions", path, page, len(items))
                for item in items:
                    if item["status_code"] not in ACTIVE_STATUSES:
                        self.skipped_status += 1
                        continue
                    if item.get("district") and item["district"] not in self.districts:
                        self.skipped_district += 1
                        continue
                    try:
                        normalized = await self._fetch_detail(item, property_type)
                        if normalized is None:
                            self.skipped_status += 1
                            continue
                        await self._save_listing(normalized)
                        self.scraped_count += 1
                        metrics.increment_scraped()
                        await asyncio.sleep(0.4)
                    except Exception as exc:
                        logger.error("Error processing %s: %s", item.get("url"), exc)
                        metrics.increment_failed()
                if not has_next:
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

    async def _fetch_detail(self, item: Dict[str, Any], property_type: str) -> Optional[Dict[str, Any]]:
        """Detail z HTML (RSC); když se objekt nenajde, zkusí veřejné JSON API."""
        html = await self._fetch(item["url"])
        try:
            return self.parse_detail_page(html, item, property_type)
        except ValueError as exc:
            logger.warning("%s: %s – falling back to JSON API", item["url"], exc)
            raw = await self._fetch(f"{API_URL}/auctions/{item['external_id']}")
            return self.normalize_auction(json.loads(raw), item, property_type)

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── RSC payload (čisté funkce) ───────────────────────────────────────────

    @staticmethod
    def _rsc_payload(html: str) -> str:
        """Spojí všechny `self.__next_f.push([1,"…"])` chunky do jednoho textu."""
        parts: List[str] = []
        for m in re.finditer(r'self\.__next_f\.push\(\[1,"((?:[^"\\]|\\.)*)"\]\)', html, re.S):
            try:
                parts.append(json.loads('"' + m.group(1) + '"'))
            except json.JSONDecodeError:
                continue
        return "".join(parts)

    @staticmethod
    def _json_object_at(text: str, start: int) -> Optional[Dict[str, Any]]:
        """Naparsuje JSON objekt začínající na `text[start] == '{'` (respektuje řetězce)."""
        depth = 0
        in_str = False
        esc = False
        for i in range(start, len(text)):
            ch = text[i]
            if in_str:
                if esc:
                    esc = False
                elif ch == "\\":
                    esc = True
                elif ch == '"':
                    in_str = False
                continue
            if ch == '"':
                in_str = True
            elif ch in "[{":
                depth += 1
            elif ch in "]}":
                depth -= 1
                if depth == 0:
                    try:
                        return json.loads(text[start:i + 1])
                    except json.JSONDecodeError:
                        return None
        return None

    @classmethod
    def _auction_objects(cls, payload: str) -> List[Dict[str, Any]]:
        """Všechny objekty `{"auction":{…}}` v payloadu (výpis: 1/položku, detail: 1)."""
        result: List[Dict[str, Any]] = []
        for m in re.finditer(r'\{"auction":\{"id":\d+', payload):
            obj = cls._json_object_at(payload, m.start())
            if obj and isinstance(obj.get("auction"), dict):
                result.append(obj["auction"])
        return result

    @staticmethod
    def _rsc_text_ref(payload: str, ref: str) -> Optional[str]:
        """Textový řádek `<id>:T<hexdélka>,<text>` pro odkaz "$<id>" (délka je v bajtech UTF-8)."""
        if not ref.startswith("$"):
            return None
        m = re.search(r"(?:^|\n)" + re.escape(ref[1:]) + r":T([0-9a-f]+),", payload)
        if not m:
            return None
        length = int(m.group(1), 16)
        data = payload.encode("utf-8")
        start = len(payload[:m.end()].encode("utf-8"))
        return data[start:start + length].decode("utf-8", errors="ignore")

    # ── parsování ────────────────────────────────────────────────────────────

    def parse_list_page(self, html: str, page: int = 1) -> Tuple[List[Dict[str, Any]], bool]:
        payload = self._rsc_payload(html)
        hrefs = {m.group(1): m.group(0) for m in re.finditer(r"/drazba/(\d+)-[a-z0-9-]+", html)}
        results: List[Dict[str, Any]] = []
        seen: set = set()
        for a in self._auction_objects(payload):
            ext_id = str(a.get("id") or "")
            if not ext_id or ext_id in seen:
                continue
            seen.add(ext_id)
            results.append({
                "external_id": ext_id,
                "url": urljoin(BASE_URL, hrefs.get(ext_id, f"/drazba/{ext_id}")),
                "title": (a.get("name") or "")[:200],
                "status_code": a.get("statusCode") or "",
                "region": a.get("region"),
                "district": a.get("county"),
                "start": a.get("start"),
                "lowest_submission": a.get("lowestSubmission"),
                "initial_price": a.get("initialPrice"),
            })
        has_next = f"page={page + 1}" in html
        return results, has_next

    def parse_detail_page(self, html: str, item: Dict[str, Any], property_type: str) -> Optional[Dict[str, Any]]:
        payload = self._rsc_payload(html)
        auctions = [a for a in self._auction_objects(payload) if "categories" in a or "auctioneer" in a]
        if not auctions:
            raise ValueError("auction object not found in RSC payload")
        auction = auctions[0]
        desc = auction.get("description")
        if isinstance(desc, str) and desc.startswith("$"):
            auction["description"] = self._rsc_text_ref(payload, desc) or ""
        return self.normalize_auction(auction, item, property_type)

    @staticmethod
    def _to_float(value: Any) -> Optional[float]:
        if value is None or value == "":
            return None
        try:
            return float(str(value).replace(" ", "").replace("\xa0", "").replace(",", "."))
        except ValueError:
            return None

    @staticmethod
    def _parse_datetime(value: Optional[str]) -> Optional[datetime]:
        """'2026-10-13T11:00:00.000' (lokální čas webu) → aware datetime Europe/Prague."""
        if not value:
            return None
        try:
            return datetime.fromisoformat(value[:19]).replace(tzinfo=PRAGUE)
        except ValueError:
            return None

    @staticmethod
    def _fmt_czk(value: Optional[float]) -> str:
        return f"{int(value):,}".replace(",", " ") + " Kč" if value else ""

    @staticmethod
    def _html_to_text(html: Optional[str]) -> str:
        if not html:
            return ""
        soup = BeautifulSoup(html, "html.parser")
        text = soup.get_text("\n", strip=True)
        return re.sub(r"\n{3,}", "\n\n", text)

    def _property_type(self, auction: Dict[str, Any], default: str) -> str:
        codes = [c for c in (auction.get("categories") or []) if isinstance(c, str)]
        for code in reversed(codes):
            if code in SUBCATEGORY_MAP:
                return SUBCATEGORY_MAP[code]
            if _RE_DISPOSITION_CODE.match(code) or code in ("6_more", "atypical", "room"):
                return "Byt"
        for code in codes:
            if code in CATEGORY_MAP:
                return CATEGORY_MAP[code]
        return default

    @staticmethod
    def _disposition(auction: Dict[str, Any]) -> Optional[str]:
        for code in auction.get("categories") or []:
            m = _RE_DISPOSITION_CODE.match(str(code))
            if m:
                return f"{m.group(1)}+{m.group(2)}"
        return None

    def normalize_auction(self, auction: Dict[str, Any], item: Dict[str, Any], property_type: str) -> Optional[Dict[str, Any]]:
        """Objekt dražby (RSC/API) → dict pro upsert_listing; None = neaktivní dražba."""
        status = auction.get("statusCode") or item.get("status_code") or ""
        if status not in ACTIVE_STATUSES:
            logger.debug("Skip %s – status %s", auction.get("id"), status)
            return None

        ext_id = str(auction.get("id") or item.get("external_id"))
        title = re.sub(r"\s+", " ", auction.get("name") or item.get("title") or "").strip()
        attrs = auction.get("biddingMethodAttributes") or {}
        lowest = (self._to_float(attrs.get("lowestSubmission")) or self._to_float(item.get("lowest_submission"))
                  or self._to_float(attrs.get("initialPrice")) or self._to_float(item.get("initial_price")))
        estimated = self._to_float(attrs.get("estimatedPrice"))
        deposit = self._to_float(auction.get("auctionSecurity"))
        min_bid = self._to_float(attrs.get("minBid"))
        start = self._parse_datetime(auction.get("start") or item.get("start"))
        finish = self._parse_datetime(auction.get("finish"))

        district = auction.get("county") or item.get("district") or None
        address = (auction.get("address") or "").replace(", Česko", "").strip(" ,")
        municipality: Optional[str] = None
        m = _RE_ZIP_CITY.search(address)
        if m:
            municipality = m.group(1).strip()
        else:
            m = _RE_TITLE_MUNICIPALITY.search(title)
            if m:
                municipality = m.group(1).strip()
        location_parts = [address] if address else ([municipality] if municipality else [])
        if district:
            location_parts.append(f"okres {district}")
        elif auction.get("region"):
            location_parts.append(auction["region"])
        location_text = ", ".join(location_parts) or "Jihomoravský kraj"

        ptype = self._property_type(auction, property_type)
        disposition = self._disposition(auction) if ptype == "Byt" else None

        description = self._html_to_text(auction.get("description"))
        info: List[str] = []
        if auction.get("typeLocalized"):
            info.append(f"Typ dražby: {auction['typeLocalized']}")
        if auction.get("number"):
            info.append(f"Číslo dražby: {auction['number']}")
        if lowest:
            info.append(f"Nejnižší podání: {self._fmt_czk(lowest)}")
        if estimated:
            info.append(f"Odhadní cena: {self._fmt_czk(estimated)}")
        if deposit:
            info.append(f"Dražební jistota: {self._fmt_czk(deposit)}")
        if min_bid:
            info.append(f"Minimální příhoz: {self._fmt_czk(min_bid)}")
        if start:
            line = f"Termín dražby: {start.strftime('%d. %m. %Y %H:%M')}"
            if finish:
                line += f" (konec nejdříve {finish.strftime('%d. %m. %Y %H:%M')})"
            info.append(line)
        auctioneer = auction.get("auctioneer") or {}
        if auctioneer.get("title"):
            who = auctioneer["title"] + (f" ({auctioneer['name']})" if auctioneer.get("name") else "")
            info.append(f"Dražebník: {who}")
        if auction.get("toursDesc"):
            info.append(f"Prohlídky: {auction['toursDesc']}")
        if auction.get("easementDesc"):
            info.append(f"Věcná břemena: {auction['easementDesc'].strip()}")
        full_description = (description + "\n\n" + "\n".join(info)).strip()[:5000]

        photos: List[str] = []
        for snip in auction.get("imageSnippets") or []:
            img_id = snip.get("id") if isinstance(snip, dict) else None
            if not img_id:
                continue
            url = f"{CDN_URL}/auctions/{ext_id}/images/{img_id}"
            if snip.get("created"):
                url += f"?created={snip['created']}"
            photos.append(url)
        if not photos:
            photos.append(f"{CDN_URL}/auctions/{ext_id}/images/main")

        area_land: Optional[float] = None
        if ptype == "Pozemek":
            m = _RE_AREA_M2.search(title) or _RE_AREA_M2.search(description)
            if m:
                area_land = self._to_float(m.group(1))

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": ext_id,
            "url": item.get("url") or f"{BASE_URL}/drazba/{ext_id}",
            "title": title[:200],
            "description": full_description,
            "property_type": ptype,
            "offer_type": "Dražba",
            "price": lowest,
            "location_text": location_text[:200],
            "municipality": (municipality or "")[:100] or None,
            "district": district,
            "area_built_up": None,
            "area_land": area_land,
            "disposition": disposition,
            "rooms": int(disposition[0]) if disposition and disposition[0].isdigit() else None,
            "photos": photos[:50],
            "auction_date": start,
            "auction_starting_price": lowest,
            "auction_deposit": deposit,
        }
        lat, lon = self._to_float(auction.get("lat")), self._to_float(auction.get("lon"))
        if lat and lon:
            result["latitude"], result["longitude"] = lat, lon
        return result

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
