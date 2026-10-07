"""Reas.cz (core/scrapers/reas_scraper.py) nad uloženými stránkami v tests/fixtures/reas – bez HTTP a DB."""
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.filters import FilterManager
from core.scrapers import reas_scraper
from core.scrapers.reas_scraper import INCREMENTAL_PAGES, LISTS, ReasScraper

FIXTURES = Path(__file__).parent / "fixtures" / "reas"


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _next_data_html(page_props: Dict[str, Any]) -> str:
    data = {"props": {"pageProps": page_props}}
    return f'<html><script id="__NEXT_DATA__" type="application/json">{json.dumps(data)}</script></html>'


def _ad(ads: List[Dict[str, Any]], location: str) -> Dict[str, Any]:
    return next(a for a in ads if a["formattedLocation"] == location)


@pytest.fixture(scope="module")
def scraper() -> ReasScraper:
    return ReasScraper()


@pytest.fixture(scope="module")
def domy_znojmo() -> List[Dict[str, Any]]:
    _, ads = ReasScraper.parse_list_page(_load("list_domy_okres_znojmo.html"), "znojmo")
    return ads


class TestReasVypisy:
    def test_vypisy_pokryvaji_tri_okresy_a_brno_mesto_jen_byty(self):
        assert {d for _, _, d in LISTS} == {"Znojmo", "Brno-venkov", "Brno-město", "Břeclav"}
        assert [seg for seg, _, d in LISTS if d == "Brno-město"] == ["byty"]
        for district in ("Znojmo", "Brno-venkov", "Břeclav"):
            assert {seg for seg, _, d in LISTS if d == district} == {"domy", "byty", "stavebni-pozemky"}

    def test_url_prvni_stranky_je_vypis_okresu_bez_cenoveho_stropu(self):
        url = ReasScraper.list_url("domy", "znojmo", 1)
        assert url.startswith("https://www.reas.cz/prodej/domy/okres-znojmo?")
        assert "cena-do" not in url
        assert "listPage" not in url

    def test_dalsi_stranky_se_strankuji_parametrem_listPage(self):
        url = ReasScraper.list_url("stavebni-pozemky", "brno-venkov", 3)
        assert "/prodej/stavebni-pozemky/okres-brno-venkov?" in url
        assert "listPage=3" in url
        # `page=` web ignoruje a vrací první stránku
        assert "&page=" not in url and "?page=" not in url

    def test_stranka_vypisu_vraci_celkovy_pocet_a_deset_inzeratu(self):
        count, ads = ReasScraper.parse_list_page(_load("list_domy_okres_znojmo.html"), "znojmo")
        assert count == 73
        assert len(ads) == 10

    def test_druha_stranka_ma_stejny_celkovy_pocet(self):
        count, ads = ReasScraper.parse_list_page(_load("list_domy_okres_znojmo_strana2.html"), "znojmo")
        assert count == 73
        assert len(ads) == 10

    def test_celostatni_nahradni_vypis_se_odmitne(self):
        # /prodej/pozemky/okres-znojmo: segment `pozemky` web nezná a vrátí 7000 inzerátů z celé ČR
        with pytest.raises(ValueError, match="okres 'znojmo'"):
            ReasScraper.parse_list_page(_load("list_neznamy_segment_celostatni.html"), "znojmo")

    def test_vypis_jineho_okresu_se_odmitne(self):
        with pytest.raises(ValueError):
            ReasScraper.parse_list_page(_load("list_domy_okres_znojmo.html"), "brno-venkov")

    def test_bez_parametru_vypisu_rozhoduje_pocet(self):
        ads = [{"_id": "a1"}]
        ok = _next_data_html({"adsListResult": {"count": 73, "data": ads}})
        assert ReasScraper.parse_list_page(ok, "znojmo") == (73, ads)
        too_many = _next_data_html({"adsListResult": {"count": 7000, "data": ads}})
        with pytest.raises(ValueError):
            ReasScraper.parse_list_page(too_many, "znojmo")

    def test_stranka_bez_dat_vypisu_je_chyba(self):
        with pytest.raises(ValueError):
            ReasScraper.parse_list_page("<html><body>Údržba</body></html>", "znojmo")


class TestReasInzerat:
    def test_dum_ma_okres_z_vypisu_a_adresu_bez_pripony_kraje(self, scraper, domy_znojmo):
        listing = scraper._build_listing(_ad(domy_znojmo, "Hrušovanská, Hrabětice"), "domy", "Znojmo")
        assert listing["district"] == "Znojmo"
        assert listing["municipality"] == "Hrabětice"
        assert listing["location_text"] == "Hrušovanská, Hrabětice"
        assert "Jihomoravský" not in listing["location_text"]
        assert listing["property_type"] == "House"
        assert listing["offer_type"] == "Sale"

    def test_adresa_bez_okresu_projde_geografickym_filtrem_diky_okresu(self, scraper, domy_znojmo):
        listing = scraper._build_listing(_ad(domy_znojmo, "Hrušovanská, Hrabětice"), "domy", "Znojmo")
        assert FilterManager().passes_search_filters(listing) is True
        without_district = dict(listing, district=None, municipality=None)
        assert FilterManager().passes_search_filters(without_district) is False

    def test_cena_je_aktualni_ne_puvodni(self, scraper, domy_znojmo):
        ad = _ad(domy_znojmo, "Nová Přímětická, Znojmo - Přímětice")
        assert ad["originalPrice"] == 6800000
        assert scraper._build_listing(ad, "domy", "Znojmo")["price"] == 6600000.0

    def test_bez_ceny_se_puvodni_cena_nepouzije(self, scraper, domy_znojmo):
        ad = dict(_ad(domy_znojmo, "Dyje 68, Dyje"), price=None, originalPrice=6290000)
        assert scraper._build_listing(ad, "domy", "Znojmo")["price"] is None
        ad["price"] = 0
        assert scraper._build_listing(ad, "domy", "Znojmo")["price"] is None

    @pytest.mark.parametrize("location, municipality", [
        ("Dyje 68, Dyje", "Dyje"),
        ("Vrbovec, okres Znojmo", "Vrbovec"),
        ("Nová Přímětická, Znojmo - Přímětice", "Znojmo"),
        ("A. Muchy, Hrušovany nad Jevišovkou", "Hrušovany nad Jevišovkou"),
        ("Hluboké Mašůvky 64, Hluboké Mašůvky", "Hluboké Mašůvky"),
    ])
    def test_obec_z_adresy_vypisu(self, scraper, domy_znojmo, location, municipality):
        assert scraper._build_listing(_ad(domy_znojmo, location), "domy", "Znojmo")["municipality"] == municipality

    def test_obec_s_pomlckou_v_nazvu(self):
        ad = {
            "formattedLocation": "Nový Šaldorf-Sedlešovice - Sedlešovice, okres Znojmo",
            "municipalitySlug": "novy-saldorf-sedlesovice",
        }
        assert ReasScraper._municipality_from_ad(ad) == "Nový Šaldorf-Sedlešovice"

    def test_obec_bez_shody_s_adresou_se_vezme_ze_slugu(self):
        ad = {"formattedLocation": "Padochov 160", "municipalitySlug": "oslavany"}
        assert ReasScraper._municipality_from_ad(ad) == "Oslavany"

    def test_plochy_a_gps_domu(self, scraper, domy_znojmo):
        listing = scraper._build_listing(_ad(domy_znojmo, "Dyje 68, Dyje"), "domy", "Znojmo")
        assert listing["area_built_up"] == 117.0
        assert listing["area_land"] == 442.0
        assert listing["latitude"] == pytest.approx(48.8471, abs=1e-4)
        assert listing["longitude"] == pytest.approx(16.1165, abs=1e-4)
        assert listing["title"] == "Prodej domu 117 m² – Dyje 68, Dyje"

    def test_popis_bez_detailu_je_prazdny_retezec(self, scraper, domy_znojmo):
        # upsert_listing popis ořezává – None by tam spadlo
        assert scraper._build_listing(domy_znojmo[0], "domy", "Znojmo")["description"] == ""

    def test_stavebni_pozemek_ma_jen_vymeru_pozemku(self, scraper):
        _, ads = ReasScraper.parse_list_page(_load("list_pozemky_okres_znojmo.html"), "znojmo")
        listing = scraper._build_listing(_ad(ads, "Nad Haltýři, Moravský Krumlov"), "stavebni-pozemky", "Znojmo")
        assert listing["property_type"] == "Land"
        assert listing["area_land"] == 1416.0
        assert listing["area_built_up"] is None
        assert listing["title"].startswith("Prodej stavebního pozemku 1416 m²")
        assert listing["municipality"] == "Moravský Krumlov"

    def test_byt_v_brne_ma_okres_brno_mesto(self, scraper):
        count, ads = ReasScraper.parse_list_page(_load("list_byty_okres_brno_mesto.html"), "brno-mesto")
        assert count == 98
        listing = scraper._build_listing(_ad(ads, "Cejl 467/65, Brno - Zábrdovice"), "byty", "Brno-město")
        assert listing["property_type"] == "Apartment"
        assert listing["district"] == "Brno-město"
        assert listing["municipality"] == "Brno"
        assert listing["price"] == 6500000.0  # originalPrice 6 700 000

    def test_chata_se_pozna_podle_subtype(self, scraper, domy_znojmo):
        ad = dict(domy_znojmo[0], type="building", subType="hut")
        assert scraper._build_listing(ad, "domy", "Znojmo")["property_type"] == "Cottage"

    def test_neznamy_typ_se_ridi_segmentem_vypisu(self, scraper, domy_znojmo):
        ad = dict(domy_znojmo[0], type="building", subType="project")
        assert scraper._build_listing(ad, "domy", "Znojmo")["property_type"] == "House"

    def test_fotky_jsou_serazene_originaly(self, scraper, domy_znojmo):
        photos = scraper._build_listing(domy_znojmo[0], "domy", "Znojmo")["photos"]
        assert photos and all(p.startswith("https://") for p in photos)


class TestReasDetail:
    def test_detail_dava_popis_a_obec_s_diakritikou(self):
        detail = ReasScraper._parse_detail(_load("detail_dum_hluboke_masuvky.html"))
        assert detail["description"].startswith("Rodinný dům s krbem")
        assert detail["municipality"] == "Hluboké Mašůvky"

    def test_obec_z_detailu_ma_prednost(self, scraper, domy_znojmo):
        ad = dict(domy_znojmo[0], municipalitySlug="neco-jineho")
        listing = scraper._build_listing(ad, "domy", "Znojmo", {"description": "Popis", "municipality": "Dyje"})
        assert listing["municipality"] == "Dyje"
        assert listing["description"] == "Popis"

    def test_detail_bez_dat_nespadne(self):
        assert ReasScraper._parse_detail("<html><body></body></html>") == {"description": None}


class _FakeReas(ReasScraper):
    """Scraper s podvrženým stahováním a ukládáním – žádné HTTP, žádná DB."""

    def __init__(self, pages: Dict[int, Optional[str]], **kwargs: Any) -> None:
        super().__init__(lists=[("domy", "znojmo", "Znojmo")], **kwargs)
        self.pages = pages
        self.requested: List[str] = []
        self.saved: List[Dict[str, Any]] = []
        self.touched: List[Dict[str, Any]] = []
        self.kept_seen = 0
        self.detail_html: Optional[str] = _load("detail_dum_hluboke_masuvky.html")

    async def _fetch_html(self, url: str) -> str:
        self.requested.append(url)
        if "/inzerat-" in url:
            if self.detail_html is None:
                raise RuntimeError("detail down")
            return self.detail_html
        page = int(url.split("listPage=")[1]) if "listPage=" in url else 1
        html = self.pages.get(page, self.pages.get(0))
        if html is None:
            raise RuntimeError(f"HTTP 503 on page {page}")
        return html

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        self.saved.append(listing)

    async def _touch_listing(self, listing: Dict[str, Any]) -> None:
        self.touched.append(listing)

    async def _keep_active_seen(self) -> int:
        self.kept_seen += 1
        return 0

    @property
    def list_requests(self) -> List[str]:
        return [u for u in self.requested if "/inzerat-" not in u]


@pytest.fixture(autouse=True)
def _bez_cekani(monkeypatch):
    async def _no_sleep(_seconds: float) -> None:
        return None

    monkeypatch.setattr(reas_scraper.asyncio, "sleep", _no_sleep)


class TestReasBeh:
    async def test_plny_beh_projde_vsechny_stranky_vypisu(self):
        # count=73 → 8 stránek; stránky 3+ vracejí znovu stranu 2 (duplicitní inzeráty se přeskočí)
        scraper = _FakeReas({1: _load("list_domy_okres_znojmo.html"), 0: _load("list_domy_okres_znojmo_strana2.html")})
        total = await scraper.run(full_rescan=True)

        assert len(scraper.list_requests) == 8
        assert "listPage" not in scraper.list_requests[0]
        assert [u.split("listPage=")[1] for u in scraper.list_requests[1:]] == [str(n) for n in range(2, 9)]
        ids = [listing["external_id"] for listing in scraper.saved]
        assert len(ids) == len(set(ids)) == total == 17  # 10 + 7 nových ze strany 2
        assert all(listing["district"] == "Znojmo" for listing in scraper.saved)
        assert scraper.lists_complete is True
        assert scraper.kept_seen == 0

    async def test_inkrementalni_beh_bere_jen_prvni_stranky(self):
        scraper = _FakeReas({0: _load("list_domy_okres_znojmo.html")}, fetch_details=False)
        await scraper.run(full_rescan=False)
        assert len(scraper.list_requests) == INCREMENTAL_PAGES
        assert not any("/inzerat-" in u for u in scraper.requested)

    async def test_chyba_stranky_ukonci_vypis_a_vrati_co_se_ulozilo(self):
        scraper = _FakeReas({1: _load("list_domy_okres_znojmo.html"), 2: None})
        total = await scraper.run(full_rescan=True)

        assert total == 10 == len(scraper.saved)
        assert len(scraper.list_requests) == 2  # po chybě se dál nestránkuje
        assert scraper.lists_complete is False
        # neúplný plný běh nesmí vést k deaktivaci toho, k čemu se scraper nedostal
        assert scraper.kept_seen == 1

    async def test_neuplny_inkrementalni_beh_aktivni_inzeraty_neoznacuje(self):
        scraper = _FakeReas({1: None})
        assert await scraper.run(full_rescan=False) == 0
        assert scraper.lists_complete is False
        assert scraper.kept_seen == 0

    async def test_celostatni_vypis_se_neulozi(self):
        scraper = _FakeReas({0: _load("list_neznamy_segment_celostatni.html")})
        total = await scraper.run(full_rescan=True)
        assert total == 0
        assert scraper.saved == []
        assert len(scraper.list_requests) == 1
        assert scraper.lists_complete is False

    async def test_prazdny_vypis_je_uplny(self):
        empty = _next_data_html({
            "adsListResult": {"count": 0, "data": []},
            "adsListParams": {"locality": {"districtSlug": "znojmo"}},
        })
        scraper = _FakeReas({0: empty})
        assert await scraper.run(full_rescan=True) == 0
        assert scraper.lists_complete is True

    async def test_bez_detailu_se_inzerat_jen_oznaci_jako_videny(self):
        scraper = _FakeReas({0: _load("list_domy_okres_znojmo.html")})
        scraper.detail_html = None
        total = await scraper.run(full_rescan=False)
        # popis by se přepsal prázdným – známý inzerát proto jen „vidíme"
        assert scraper.saved == []
        assert len(scraper.touched) == 10 == total

    async def test_popis_a_obec_se_berou_z_detailu(self):
        scraper = _FakeReas({0: _load("list_domy_okres_znojmo.html")})
        await scraper.run(full_rescan=False)
        assert len(scraper.saved) == 10
        assert all(listing["description"].startswith("Rodinný dům s krbem") for listing in scraper.saved)
