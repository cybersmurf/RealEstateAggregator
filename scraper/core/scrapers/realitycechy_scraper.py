"""
RealityCechy.cz scraper – celostátní inzertní portál (Nette, SSR HTML).

Strategie: httpx + BeautifulSoup. Výpis /nemovitosti/prodej-domu/ (resp.
prodej-bytu/, prodej-pozemku/) umí filtrovat po okresech přes query parametr
`okresy_id[0]=<id>` (ID = kraj 2002 + kód okresu: Znojmo 20023713,
Brno-venkov 20023703, Brno-město 20023702; zjištěno z odkazu
„/hledat/nabidky?okresy_id…“ na stránce /nemovitosti/okres/<slug>).
Stránkování `?vp-page=N`. Od redesignu (zjištěno 6. 11. 2026) má stránka 20 karet
`article.rc-card[data-id]` (cena `.rc-card__price`, obec `.rc-card__loc`) a celkový
počet v `.rc-toolbar__count` („200 výsledků“); stránka za poslední vrací znovu
poslední stránku. Původní značkování (24 karet `div.nem-item[id]`, počet
v `.pocet-inzeratu .cislo`) a ld+json ItemList (jen URL + název) zůstávají jako záloha.

Dvoufázový běh (jako iDNES): napřed všechny stránky všech 12 výpisů (114 stránek
na ~2 140 nabídek, ~4 min), známé inzeráty se jen označí jako viděné
(`touch_listings`), detail se stahuje jen u nových a u těch, jejichž cena ve výpisu
se liší od uložené – v časovém rozpočtu. Do 6. 11. 2026 se stahoval detail
u každé položky (~2 100 detailů), plný běh přetahoval 45min limit úlohy, runner
ho zrušil a nic se nedeaktivovalo (od 15. 10. 2026 43 % aktivních inzerátů neviděno).

Filtry (od 7. 11. 2026): karta výpisu, která neprojde filtry (cena nad limit, obec okresu
Břeclav mimo Pálavu), se nestahuje; známý inzerát s takovou kartou se deaktivuje a jako
viděný se neoznačí – dřív zůstal aktivní se starou cenou. U známých rozhoduje uložený typ
z detailu (komerční pozemek ve výpisu pozemků limit pozemků nemá). Co projde kartou, ale
vyřadí ho detail, si pamatujeme v scrape_skips (60 dní), ať se detail nestahuje každou noc.
Stažený inzerát, který je ve výpisu znovu, dostane detail místo „viděno“.

Detail: h1.entry-title, h2.mesto-ulice, tabulky „Adresa“ (Obec/Okres/Kraj)
a „Informace“ (Cena, Stav, Druh, Typ, Zastavěná plocha, Výměra pozemku,
Užitná/Obytná plocha, Postaveno z), popis p.realita-popis, galerie
li[data-fancybox] data-src=/foto/hq/<bucket>/<id>/<id>_NN.webp (plná velikost), GPS ve
skriptu `var x = lat; var y = lon;` (bývá střed obce). ld+json
RealEstateListing má jen zkrácený popis a mq fotky – používáme jako zálohu.
Smazaný inzerát vrací 200: blok `.smazany_inzerat` („Nemovitost již není v nabídce“)
a pod ním karty `.rc-card` CIZÍCH podobných nabídek – takový detail se neukládá,
inzerát se deaktivuje.

Proč: 30. 9. 2026 hlásil RealityCechy 871 rodinných domů v JMK (170 domů,
74 bytů a 143 pozemků v okrese Znojmo); inzerují sem i menší RK bez Sreality.
robots.txt povoluje /nemovitosti/ i /nemovitost/ pro běžné klienty
(blokuje jen tréninkové AI boty podle User-Agentu).
"""
import asyncio
import json
import logging
import math
import re
import time
from typing import Any, Container, Dict, List, Optional, Tuple
from urllib.parse import urljoin

import httpx
from bs4 import BeautifulSoup, Tag

from ..utils import timer, scraper_metrics_context
from ..database import get_db_manager
from ..filters import get_filter_manager
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

LIST_DELAY_SECONDS = 1.0
DETAIL_DELAY_SECONDS = 0.4
# Úplný výpis musí ukázat aspoň tolik z počtu, který portál hlásí; méně = stránkování se rozbilo
# (posun nabídek během procházení vysvětlí jednotky, ne desítky procent)
MIN_LIST_COVERAGE = 0.9

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
COTTAGE_RE = re.compile(r"chat|chalup", re.I)
# „180 Kč/m2“ – RK zadala u pozemku cenu za m²; do 10. 11. 2026 se ukládala jako celková
PER_M2_RE = re.compile(r"Kč\s*/\s*m\s*(?:2|²)|za\s*m\s*(?:2|²)", re.I)
DELETED_SELECTOR = ".smazany_inzerat, .smazany_inzerat_cena, .smazany_inzerat_popis"
DELETED_TEXT = "nemovitost již není v nabídce"
# Důvod v scrape_skips: výpisem prošel, detail ho vyřadil filtrem – detail se 60 dní nestahuje
SKIP_FILTERED = "filtered out by detail"


class RealityCechyScraper:
    """Scraper pro realitycechy.cz (SSR HTML, filtr po okresech)."""

    SOURCE_CODE = "REALITYCECHY"

    # Nejdelší výpis (byty Brno-město, 503 nabídek) má 26 stránek; strop je jen pojistka.
    # Do 6. 11. 2026 byl plný běh omezen na 20 stránek – zbytek výpisu by se deaktivoval.
    MAX_LIST_PAGES = 100
    INCREMENTAL_LIST_PAGES = 3      # inkrementální běh: jen první (nejnovější) stránky
    # Limit úlohy v runneru je 45 min (asyncio.wait_for); kdo ho přetáhne, je zrušen a po
    # plném běhu se nic nedeaktivuje. Výpisy trvají ~4 min, detail ~1 s.
    TIME_BUDGET_SECONDS = 36 * 60

    def __init__(self, lists: Optional[List[Tuple[str, str, str, str, int]]] = None) -> None:
        self.lists = lists or DEFAULT_LISTS
        self.scraped_count = 0
        # False = některý výpis se nepodařilo projít celý; co jsme neviděli, nemusí být stažené
        self.lists_complete = True
        self.deleted_count = 0
        self.retired_count = 0      # deaktivované v tomto běhu (smazané, mimo filtry)
        self._http_client: Optional[httpx.AsyncClient] = None

    async def run(self, full_rescan: bool = False) -> int:
        """
        Vstup z runneru. Vrací počet inzerátů viděných ve výpisech (známé obnovené + nově
        uložené) – runner po plném běhu deaktivuje neviděné jen tehdy, když je číslo > 0
        a neviděných není víc než viděných.
        """
        max_pages = self.MAX_LIST_PAGES if full_rescan else self.INCREMENTAL_LIST_PAGES
        return await self.scrape(max_pages=max_pages)

    async def scrape(self, max_pages: int = INCREMENTAL_LIST_PAGES) -> int:
        logger.info("Starting RealityCechy scraper (max_pages=%s, lists=%s)", max_pages, len(self.lists))
        started = time.monotonic()
        self.scraped_count = 0
        self.deleted_count = 0
        self.retired_count = 0
        self.lists_complete = True
        with scraper_metrics_context() as metrics:
            async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
                self._http_client = client
                try:
                    self.scraped_count = await self._scrape_two_phase(max_pages, started, metrics)
                except Exception as exc:
                    logger.error("RealityCechy scraping failed: %r", exc)
                    metrics.increment_failed()
                finally:
                    self._http_client = None
        logger.info("RealityCechy scraper done. Seen %s listings", self.scraped_count)
        return self.scraped_count

    async def _scrape_two_phase(self, max_pages: int, started: float, metrics: Any) -> int:
        db = get_db_manager()
        listings = await db.get_known_listings(self.SOURCE_CODE)
        known = {external_id: row["price"] for external_id, row in listings.items()}
        stored_types = {external_id: row["property_type"] for external_id, row in listings.items() if row["property_type"]}
        inactive = {external_id for external_id, row in listings.items() if not row["is_active"]}
        skipped = await db.get_skip_reasons(self.SOURCE_CODE)

        # 1. fáze: všechny stránky výpisů (levné)
        with timer("Fetch RealityCechy lists"):
            items = await self._crawl_lists(max_pages, started, metrics)
        if not items:
            logger.warning("No listings found in RealityCechy lists")
            return 0

        # Co neprojde filtry už podle karty výpisu, se nestahuje. Známý inzerát mimo filtry (cena
        # stoupla nad limit) se deaktivuje – dřív se každou noc jen označil jako viděný a zůstal
        # aktivní se starou cenou (7. 11. 2026: 17 inzerátů). Rozhoduje uložený typ z detailu.
        in_scope, out_of_scope_known = self.split_by_filters(items, known, stored_types, skipped)

        # Známé aktivní inzeráty stačí „vidět" (a doplnit jim obec a okres z výpisu, kde chybí).
        # Stažený (mimo filtry, smazaný, neviděný), který se ve výpisu znovu objevil, dostane detail:
        # „viděno" by ho oživilo se starými daty.
        touched = await db.touch_listings(
            self.SOURCE_CODE,
            [(i["external_id"], i["municipality"] or None, i["district"]) for i in in_scope
             if i["external_id"] in known and i["external_id"] not in inactive],
        )
        for item in out_of_scope_known:
            reason = skipped.get(item["external_id"]) or "out of search filters (list card)"
            if await db.deactivate_listing(self.SOURCE_CODE, item["external_id"], reason=reason):
                self.retired_count += 1

        # Neúplný výpis: runner po plném běhu deaktivuje vše, co jsme „neviděli" –
        # aktivní inzeráty proto necháme viděné a o stažení rozhodne až úplný běh.
        if not self.lists_complete:
            kept = await db.mark_active_seen(self.SOURCE_CODE)
            logger.warning("RealityCechy lists incomplete – %s active listings kept as seen, nothing will be deactivated", kept)

        # 2. fáze: detail jen u nových a u změněné ceny, v časovém rozpočtu. Co se nestihne, je
        # nové (v DB není, nemá co deaktivovat) nebo známé a už obnovené – další běh pokračuje.
        details = self.select_for_detail(in_scope, known, revalidate=inactive)
        logger.info(
            "RealityCechy lists: %s listings, %s known refreshed, %s known out of search filters or remembered "
            "as filtered (%s deactivated now), %s need detail",
            len(items), touched, len(out_of_scope_known), self.retired_count, len(details),
        )

        saved_new = 0
        filtered_out: List[str] = []
        for idx, item in enumerate(details):
            if self.out_of_time(started, time.monotonic()):
                logger.info("RealityCechy time budget used up after %s details – %s left for the next run",
                            idx, len(details) - idx)
                break
            external_id = item["external_id"]
            try:
                start = time.perf_counter()
                html = await self._fetch(item["url"])
                metrics.record_fetch(time.perf_counter() - start)
                if self.is_deleted_page(html):
                    self.deleted_count += 1
                    if external_id in known:
                        if await db.deactivate_listing(self.SOURCE_CODE, external_id,
                                                       reason="deleted on portal (soft 404)"):
                            self.retired_count += 1
                    logger.info("RealityCechy listing %s is no longer offered – not saved", item["url"])
                else:
                    normalized = self.parse_detail_page(html, item, item["property_type"], item["offer_type"], item["district"])
                    if normalized and await self._save_listing(normalized) is not None:
                        metrics.increment_scraped()
                        if external_id not in known or external_id in inactive:
                            saved_new += 1      # nový nebo oživený stažený – touch ho nezapočítal
                    elif normalized:
                        # Výpisem prošel, detail ho vyřadil (typ nebo okres z detailu): zapamatovat,
                        # ať se nestahuje každou noc; známý se starými daty aktivní nezůstane
                        filtered_out.append(external_id)
                        if external_id in known and await db.deactivate_listing(
                                self.SOURCE_CODE, external_id, reason="out of search filters (detail)"):
                            self.retired_count += 1
            except Exception as exc:
                logger.error("Error processing %s: %s", item.get("url"), exc)
                metrics.increment_failed()
            await asyncio.sleep(DETAIL_DELAY_SECONDS)

        await db.remember_skipped(self.SOURCE_CODE, filtered_out, SKIP_FILTERED)
        logger.info("RealityCechy details: %s new saved, %s deleted on the portal, %s filtered out by detail, "
                    "%s deactivated", saved_new, self.deleted_count, len(filtered_out), self.retired_count)
        return touched + saved_new

    async def _crawl_lists(self, max_pages: int, started: float, metrics: Any) -> List[Dict[str, Any]]:
        """Projde všechny výpisy a vrátí položky bez duplicit, každou s typem, nabídkou a okresem výpisu."""
        items: List[Dict[str, Any]] = []
        seen: set = set()
        for path, property_type, offer_type, district, okres_id in self.lists:
            if self.out_of_time(started, time.monotonic()):
                logger.warning("RealityCechy time budget used up while crawling lists")
                self.lists_complete = False
                break
            list_items = await self._crawl_list(path, property_type, offer_type, district, okres_id,
                                                max_pages, started, metrics)
            for item in list_items:
                if item["external_id"] not in seen:
                    seen.add(item["external_id"])
                    items.append(item)
        return items

    async def _crawl_list(self, path: str, property_type: str, offer_type: str, district: str, okres_id: int,
                          max_pages: int, started: float, metrics: Any) -> List[Dict[str, Any]]:
        items: List[Dict[str, Any]] = []
        list_seen: set = set()
        total = 0
        last_page = max_pages
        whole_list = False   # podle počtu z 1. stránky máme projít celý výpis (ne jen první stránky)
        complete = True
        page = 1
        while page <= last_page:
            if page > 1 and self.out_of_time(started, time.monotonic()):
                complete = False
                break
            url = self.build_list_url(path, okres_id, page)
            try:
                start = time.perf_counter()
                html = await self._fetch(url)
                metrics.record_fetch(time.perf_counter() - start)
            except Exception as exc:
                # nedostupná stránka nesmí shodit běh, ale co je za ní, jsme neviděli
                logger.error("RealityCechy list %s failed: %r", url, exc)
                metrics.increment_failed()
                complete = False
                break

            page_items, page_total, has_next = self.parse_list_page(html, page)
            if page == 1:
                total = page_total
                needed = self.pages_needed(total, len(page_items))
                if needed:
                    last_page = min(needed, max_pages)
                    whole_list = needed <= max_pages
                    if needed > max_pages and max_pages >= self.MAX_LIST_PAGES:
                        complete = False
                elif not page_items and "rc-toolbar__count" not in html and "pocet-inzeratu" not in html:
                    complete = False   # ani karty, ani počet: neznámá stránka, ne prázdný výpis

            fresh = [i for i in page_items if i["external_id"] not in list_seen]
            if not fresh:
                break   # za poslední stránkou portál vrací znovu poslední stránku
            for item in fresh:
                list_seen.add(item["external_id"])
                item.update(property_type=property_type, offer_type=offer_type, district=district)
                items.append(item)
            if not total and not has_next:
                break
            page += 1
            await asyncio.sleep(LIST_DELAY_SECONDS)

        if whole_list and len(list_seen) < total * MIN_LIST_COVERAGE:
            complete = False   # stránkování nevrátilo, co portál hlásí (např. ignoruje vp-page)
        if not complete:
            self.lists_complete = False
            logger.warning("RealityCechy list %s %s incomplete: %s of %s listings", path, district, len(list_seen), total)
        logger.info("RealityCechy %s %s: %s listings (portal says %s)", path, district, len(list_seen), total)
        return items

    @staticmethod
    def build_list_url(path: str, okres_id: int, page: int = 1) -> str:
        query = f"okresy_id%5B0%5D={okres_id}"
        if page > 1:
            query = f"vp-page={page}&{query}"
        return f"{urljoin(BASE_URL, path)}?{query}"

    @http_retry
    async def _fetch(self, url: str) -> str:
        if self._http_client is None:
            raise RuntimeError("HTTP client not initialized")
        response = await self._http_client.get(url)
        response.raise_for_status()
        return response.text

    # ── rozhodování o běhu (čisté funkce, bez HTTP a DB) ─────────────────────

    @classmethod
    def out_of_time(cls, started: float, now: float) -> bool:
        """Vyčerpal běh časový rozpočet? (čas od startu včetně procházení výpisů)"""
        return now - started >= cls.TIME_BUDGET_SECONDS

    @staticmethod
    def pages_needed(total: int, per_page: int) -> int:
        """Kolik stránek má výpis s `total` nabídkami při `per_page` kartách na stránku (0 = neznámo)."""
        if total <= 0 or per_page <= 0:
            return 0
        return max(1, math.ceil(total / per_page))

    @staticmethod
    def select_for_detail(items: List[Dict[str, Any]], known: Dict[str, Optional[float]],
                          revalidate: Container[str] = ()) -> List[Dict[str, Any]]:
        """
        Které položky potřebují detail: napřed známé se změněnou cenou (ať se zapíše do historie)
        a známé stažené, které jsou ve výpisu znovu (`revalidate`), potom nové. Známé se stejnou
        cenou nebo bez ceny ve výpisu („cena v RK") detail nepotřebují. Cena za m² (`unit_price`)
        se s uloženou celkovou porovnat nedá – detail jen tehdy, když je uložená cena rovna té
        jednotkové (řádek z doby, kdy se jednotková ukládala jako celková).
        """
        changed: List[Dict[str, Any]] = []
        new: List[Dict[str, Any]] = []
        for item in items:
            if item["external_id"] not in known:
                new.append(item)
                continue
            old_price, list_price = known[item["external_id"]], item.get("price")
            unit_price = item.get("unit_price")
            if item["external_id"] in revalidate or (
                    list_price is not None and (old_price is None or abs(float(old_price) - list_price) > 0.5)) or (
                    unit_price is not None and old_price is not None and abs(float(old_price) - unit_price) <= 0.5):
                changed.append(item)
        return changed + new

    @staticmethod
    def passes_filters(item: Dict[str, Any], stored_type: Optional[str] = None) -> bool:
        """
        Projde položka výpisu cenovými, typovými a místními filtry (settings.yaml)? Co by upsert
        stejně zahodil (dům nad limit, obec okresu Břeclav mimo Pálavu), nemá smysl stahovat –
        a stahovalo by se každou noc znovu, protože se to nikdy neuloží. Chata ve výpisu domů
        (typ se pozná až z detailu) nemá cenový limit, proto se pozná už z titulku.

        `stored_type` = typ, který u známého inzerátu určil detail (House, Commercial, Cottage…).
        Má přednost před typem výpisu: komerční pozemek za 18 mil. ve výpisu pozemků limit
        pozemků nemá (7. 11. 2026: 18 z 35 známých inzerátů by jinak vypadlo neprávem).
        """
        property_type = stored_type or item.get("property_type") or ""
        if property_type == "Dům" and COTTAGE_RE.search(item.get("title") or ""):
            property_type = "Chata"
        municipality = item.get("municipality") or ""
        district = item.get("district") or ""
        location = ", ".join(p for p in (municipality, f"okres {district}" if district else "") if p)
        return get_filter_manager().passes_search_filters({
            "property_type": property_type,
            "offer_type": item.get("offer_type") or "Prodej",
            "price": item.get("price"),
            "location_text": location,
            "municipality": municipality,
            "district": district,
        })

    @classmethod
    def split_by_filters(cls, items: List[Dict[str, Any]], known: Container[str],
                         stored_types: Dict[str, str], skipped: Container[str] = ()
                         ) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
        """
        Položky výpisu → (projdou filtry, známé mimo filtry k deaktivaci). Nové položky mimo filtry
        a zapamatované jako vyřazené detailem (`skipped`, scrape_skips 60 dní) v první skupině
        nejsou – detail se nestahuje. Typ známého inzerátu je uložený typ z detailu (`stored_types`).

        Známý inzerát s kartou bez obce (záloha ld+json při změně značkování) se nikdy
        nedeaktivuje: o lokalitě nejde rozhodnout a filtr částečného okresu by jinak vyřadil
        celý okres Břeclav.
        """
        in_scope: List[Dict[str, Any]] = []
        out_of_scope_known: List[Dict[str, Any]] = []
        for item in items:
            external_id = item["external_id"]
            is_known = external_id in known
            if external_id in skipped:
                if is_known:
                    out_of_scope_known.append(item)
                continue
            if cls.passes_filters(item, stored_types.get(external_id)):
                in_scope.append(item)
            elif is_known and not item.get("municipality"):
                in_scope.append(item)
            elif is_known:
                out_of_scope_known.append(item)
        return in_scope, out_of_scope_known

    @staticmethod
    def _is_deleted(soup: BeautifulSoup) -> bool:
        if soup.select_one(DELETED_SELECTOR) is not None:
            return True
        return DELETED_TEXT in re.sub(r"\s+", " ", soup.get_text(" ")).lower()

    @classmethod
    def is_deleted_page(cls, html: str) -> bool:
        """
        Smazaný inzerát: portál vrací 200 s blokem „Nemovitost již není v nabídce“ a pod ním
        karty podobných (cizích) nabídek. Takovou stránku nesmíme uložit jako inzerát.
        """
        if "smazany_inzerat" not in html and "nabídce" not in html:
            return False   # rychlá cesta: živý detail nemá ani třídu, ani text
        return cls._is_deleted(BeautifulSoup(html, "html.parser"))

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

    @staticmethod
    def _price_text(el: Optional[Tag]) -> str:
        """Text ceny bez přeškrtnuté původní ceny (<del>), kdyby ji portál u slevy ukázal."""
        if el is None:
            return ""
        for old in el.select("del, s, strike"):
            old.decompose()
        return re.sub(r"\s+", " ", el.get_text(" ", strip=True)).strip()

    @classmethod
    def _card_prices(cls, price_text: str) -> Dict[str, Optional[float]]:
        """Cena z karty výpisu: celková → `price`, „180 Kč/m2“ → jen `unit_price` (celková není známá)."""
        number = cls._parse_number(price_text)
        if number is not None and PER_M2_RE.search(price_text or ""):
            return {"price": None, "unit_price": number}
        return {"price": number, "unit_price": None}

    def parse_list_page(self, html: str, page: int = 1) -> Tuple[List[Dict[str, Any]], int, bool]:
        """
        Stránka výpisu → (položky, celkový počet nabídek podle portálu, existuje další stránka).
        Položka: external_id, url, title, municipality, price_text, price, status, thumb.
        """
        soup = BeautifulSoup(html, "html.parser")
        results: List[Dict[str, Any]] = []
        seen: set = set()

        # redesign (11/2026): article.rc-card v kontejneru [data-rc-cards]
        container = soup.select_one("[data-rc-cards]") or soup
        for card in container.select("article.rc-card[data-id]"):
            external_id = (card.get("data-id") or "").strip()
            link = card.select_one(".rc-card__title a[href]") or card.select_one("a[href*='/nemovitost/']")
            if not external_id.isdigit() or not link or external_id in seen:
                continue
            seen.add(external_id)
            loc = card.select_one(".rc-card__loc")
            img = card.select_one(".rc-card__slide img")
            thumb = (img.get("src") or img.get("data-src") or "") if img else ""
            price_text = self._price_text(card.select_one(".rc-card__price"))
            results.append({
                "external_id": external_id,
                "url": urljoin(BASE_URL, link.get("href", "")),
                "title": self._text_with_sup(link)[:200],
                "municipality": self._clean_municipality(loc.get_text(" ", strip=True))[:100] if loc else "",
                "price_text": price_text,
                **self._card_prices(price_text),
                "status": "",
                "thumb": urljoin(BASE_URL, thumb) if thumb else "",
            })

        # původní značkování (do 10/2026)
        if not results:
            for card in soup.select("div.nem-item[id]"):
                external_id = (card.get("id") or "").strip()
                link = card.select_one(".nem-headline a[href]") or card.select_one("a[href*='/nemovitost/']")
                if not external_id.isdigit() or not link or external_id in seen:
                    continue
                seen.add(external_id)
                addr = card.select_one(".nem-ulice-mesto")
                status_el = card.select_one(".nem-novinka")
                thumb_el = card.select_one(".nem-gallery-photo img[src]")
                price_text = self._price_text(card.select_one(".nem-cena-nemovitosti"))
                results.append({
                    "external_id": external_id,
                    "url": urljoin(BASE_URL, link.get("href", "")),
                    "title": self._text_with_sup(link)[:200],
                    "municipality": self._clean_municipality(addr.get_text(" ", strip=True))[:100] if addr else "",
                    "price_text": price_text,
                    **self._card_prices(price_text),
                    "status": status_el.get_text(" ", strip=True).lower() if status_el else "",
                    "thumb": urljoin(BASE_URL, thumb_el["src"]) if thumb_el else "",
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
                                    "municipality": "", "price_text": "", "price": None, "status": "", "thumb": ""})

        total_el = soup.select_one(".rc-toolbar__count") or soup.select_one(".pocet-inzeratu .cislo")
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

    @staticmethod
    def _own_photo(src: str, external_id: str) -> bool:
        """Fotka patří inzerátu: /foto/<velikost>/<bucket>/<id>/<id>_NN.webp s jeho vlastním id."""
        if "/foto/" not in src:
            return False
        return not external_id.isdigit() or f"/{external_id}/" in src

    def parse_detail_page(self, html: str, item: Dict[str, Any], property_type: str, offer_type: str,
                          district: str) -> Optional[Dict[str, Any]]:
        """
        Detail → normalizovaný inzerát; None, když portál inzerát smazal (stránka pak ukazuje
        jen „již není v nabídce“ a karty cizích podobných nabídek).
        """
        soup = BeautifulSoup(html, "html.parser")
        if self._is_deleted(soup):
            return None
        ld_listing = next((o for o in self._ld_json(soup) if o.get("@type") == "RealEstateListing"), {})

        external_id = str(item.get("external_id") or "")
        if not external_id:
            hid = soup.select_one("input#realita_id[value]")
            external_id = hid["value"] if hid else re.sub(r".*/(\d+)/?$", r"\1", item.get("url", ""))
        external_id = str(external_id)

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
        price_text = params.get("cena", "")
        price = self._parse_number(price_text)
        if price is None:
            price_text = item.get("price_text", "")
            price = self._parse_number(price_text)
        unit_price = price if price is not None and PER_M2_RE.search(price_text or "") else None
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

        price_note: Optional[str] = None
        if unit_price is not None:
            price_note = f"{unit_price:,.0f} Kč/m²".replace(",", " ")
            area = area_land if ptype == "Pozemek" else (area_built_up or area_land)
            price = float(round(unit_price * area)) if area else None

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
        # Jen vlastní galerie (li[data-fancybox], desktop i mobilní verze se stejnými soubory)
        # a jen soubory s id tohoto inzerátu – karty „Podobné nemovitosti“ (.rc-card) pod
        # detailem nesou fotky cizích nabídek.
        photos: List[str] = []
        for li in soup.select("li[data-fancybox][data-src]"):
            src = li.get("data-src") or ""
            if not self._own_photo(src, external_id):
                continue
            full = urljoin(BASE_URL, src)
            if full not in photos:
                photos.append(full)
        if not photos:
            images = ld_listing.get("image") or []
            for src in [images] if isinstance(images, str) else images:
                if not isinstance(src, str) or not self._own_photo(src, external_id):
                    continue
                full = urljoin(BASE_URL, src)
                if full not in photos:
                    photos.append(full)
        # inzerát bez fotek má ve výpisu zástupný obrázek portálu (default_foto.jpg)
        thumb = item.get("thumb") or ""
        if not photos and thumb and "default_foto" not in thumb and self._own_photo(thumb, external_id):
            photos.append(thumb)

        # ── stav / konstrukce ──
        condition = params.get("stav") or ""
        condition = condition[:1].upper() + condition[1:] if condition.lower() not in UNSET_VALUES else None
        construction = params.get("postaveno z") or ""
        construction = construction[:1].upper() + construction[1:] if construction.lower() not in UNSET_VALUES else None

        result: Dict[str, Any] = {
            "source_code": self.SOURCE_CODE,
            "external_id": external_id,
            "url": item.get("url") or ld_listing.get("url") or "",
            "title": title,
            "description": description[:5000],
            "property_type": ptype,
            "offer_type": otype,
            "price": price,
            "price_note": price_note,
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

    async def _save_listing(self, listing: Dict[str, Any]) -> Optional[Any]:
        """Upsert; None = inzerát vyřadil filtr (settings.yaml)."""
        db = get_db_manager()
        listing_id = await db.upsert_listing(listing)
        logger.info("Saved listing %s: %s | %s Kč", listing_id, listing.get("title", "")[:50], listing.get("price"))
        return listing_id
