"""
Unit testy parseru RealityMIX.cz nad uloženými fixturami (bez HTTP a DB).
"""
import sys
from pathlib import Path
from typing import Any, Dict, List

import httpx
import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers import realitymix_scraper
from core.scrapers.realitymix_scraper import RealityMixScraper

FIXTURES = Path(__file__).parent / "fixtures" / "realitymix"


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


class TestRealitymixListPage:

    def setup_method(self):
        self.scraper = RealityMixScraper()
        self.items, self.total, self.has_next = self.scraper.parse_list_page(_load("list_domy_znojmo.html"), page=1)

    def test_vypis_ma_20_polozek_plus_topovanou(self):
        assert len(self.items) == 21

    def test_celkovy_pocet_a_dalsi_stranka(self):
        assert self.total == 163
        assert self.has_next is True

    def test_external_id_z_url(self):
        ids = {i["external_id"] for i in self.items}
        assert "8709111" in ids
        assert all(i.isdigit() for i in ids)

    def test_url_vede_na_detail_bez_kotvy(self):
        for item in self.items:
            assert item["url"].startswith("https://realitymix.cz/detail/")
            assert "#" not in item["url"]
            assert "trackredir" not in item["url"]

    def test_polozky_jsou_unikatni(self):
        assert len({i["url"] for i in self.items}) == len(self.items)

    def test_adresa_a_cena_z_vypisu(self):
        item = next(i for i in self.items if i["external_id"] == "8709111")
        assert item["address"] == "Chvalovice, okr. Znojmo"
        assert item["price_text"] == "3 490 000 Kč"
        assert item["title"] == "Prodej domu/vily, 124 m²"

    def test_topovana_nabidka_mimo_okres_je_ve_vypisu(self):
        assert any("okr. Vyškov" in i["address"] for i in self.items)

    def test_prazdne_html_vraci_prazdny_seznam(self):
        items, total, has_next = self.scraper.parse_list_page("<html><body></body></html>")
        assert items == []
        assert total == 0
        assert has_next is False


class TestRealitymixDetailDum:

    def setup_method(self):
        self.scraper = RealityMixScraper()
        item = {
            "url": "https://realitymix.cz/detail/chvalovice/prodej-rodinneho-domu-124m2-k-u-chvalovice-u-znojma-8709111.html",
            "external_id": "8709111", "address": "Chvalovice, okr. Znojmo", "price_text": "3 490 000 Kč",
        }
        self.d = self.scraper.parse_detail_page(_load("detail_dum_8709111.html"), item, "Dům", "Prodej", "Znojmo")

    def test_zakladni_identifikace(self):
        assert self.d["source_code"] == "REALITYMIX"
        assert self.d["external_id"] == "8709111"
        assert self.d["url"].endswith("-8709111.html")

    def test_titulek_z_og_title(self):
        assert self.d["title"] == "Prodej rodinného domu, 124m2, k.ú. Chvalovice u Znojma"

    def test_typ_a_nabidka(self):
        assert self.d["property_type"] == "Dům"
        assert self.d["offer_type"] == "Prodej"

    def test_cena(self):
        assert self.d["price"] == 3490000.0

    def test_plochy(self):
        assert self.d["area_built_up"] == 124.0
        assert self.d["area_land"] == 442.0

    def test_okres_a_obec_z_breadcrumbs(self):
        assert self.d["district"] == "Znojmo"
        assert self.d["municipality"] == "Chvalovice"
        assert self.d["location_text"] == "Chvalovice, okres Znojmo"

    def test_gps(self):
        assert self.d["latitude"] == pytest.approx(48.78871666)
        assert self.d["longitude"] == pytest.approx(16.08229724)

    def test_fotky_jen_tohoto_inzeratu_v_plne_velikosti(self):
        assert len(self.d["photos"]) >= 10
        assert all(p.startswith("https://st.realitymix.cz/i/") and "/8709111/" in p for p in self.d["photos"])
        assert all("_nahled" not in p and "_detail" not in p for p in self.d["photos"])
        assert len(set(self.d["photos"])) == len(self.d["photos"])

    def test_stav_a_konstrukce(self):
        assert self.d["condition"] == "před rekonstrukcí"
        assert self.d["construction_type"] == "Smíšená"

    def test_popis_obsahuje_text_a_realitku(self):
        assert self.d["description"].startswith("K prodeji nabízíme rodinný dům")
        assert "Realitní kancelář: Nemovitosti Znojmo" in self.d["description"]
        assert len(self.d["description"]) <= 5000

    def test_dispozice_domu_z_popisu(self):
        assert self.d["disposition"] == "3+1"
        assert self.d["rooms"] == 3

    def test_kontakt_na_maklere(self):
        assert self.d["seller_name"]
        assert "@" in self.d["seller_email"]
        assert len("".join(c for c in self.d["seller_phone"] if c.isdigit())) >= 9
        assert self.d["seller_company"].startswith("Nemovitosti Znojmo")


class TestRealitymixDetailDrazbaBezFotek:
    """Šumná č. p. 9: dražebník inzeruje v sekci Prodej, bez fotek."""

    def setup_method(self):
        self.scraper = RealityMixScraper()
        item = {"url": "https://realitymix.cz/detail/sumna/rodinny-dum-sumna-8694091.html",
                "external_id": "8694091", "address": "Šumná, okr. Znojmo", "price_text": "5 680 000 Kč"}
        self.d = self.scraper.parse_detail_page(
            _load("detail_drazba_bez_fotek_8694091.html"), item, "Dům", "Prodej", "Znojmo")

    def test_drazba_poznana_z_popisu(self):
        assert self.d["title"] == "Rodinný dům , Šumná"
        assert self.d["offer_type"] == "Dražba"

    def test_obecny_obrazek_portalu_neni_fotka(self):
        assert self.d["photos"] == []

    def test_plochy_a_cena(self):
        assert self.d["price"] == 5680000.0
        assert self.d["area_built_up"] == 350.0
        assert self.d["area_land"] == 1662.0

    def test_kontakt_na_drazebnika(self):
        assert self.d["seller_name"] == "Call centrum exdrazby.cz"
        assert self.d["seller_email"] == "dotazy@exdrazby.cz"
        assert self.d["seller_phone"] == "+420 774 740 636"
        assert self.d["seller_company"] == "exdrazby.cz JURIS REAL Dražby, a. s."


class TestRealitymixDetailByt:

    def setup_method(self):
        self.scraper = RealityMixScraper()
        item = {"url": "https://realitymix.cz/detail/znojmo/prodej-bytu-3-kk-79-m-znojmo-8689753.html",
                "external_id": "8689753", "address": "Smutného, Znojmo", "price_text": "6 990 000 Kč"}
        self.d = self.scraper.parse_detail_page(_load("detail_byt_8689753.html"), item, "Byt", "Prodej", "Znojmo")

    def test_typ_byt(self):
        assert self.d["property_type"] == "Byt"

    def test_dispozice_a_pokoje(self):
        assert self.d["disposition"] == "3+kk"
        assert self.d["rooms"] == 3

    def test_plocha_a_cena(self):
        assert self.d["area_built_up"] == 79.0
        assert self.d["area_land"] is None
        assert self.d["price"] == 6990000.0

    def test_mesto_znojmo_bez_okr_v_adrese(self):
        assert self.d["municipality"] == "Znojmo"
        assert self.d["district"] == "Znojmo"

    def test_konstrukce_cihla(self):
        assert self.d["construction_type"] == "Cihla"
        assert self.d["condition"] == "velmi dobrý"

    def test_fotky(self):
        assert len(self.d["photos"]) >= 5
        assert all("/8689753/" in p for p in self.d["photos"])


class TestRealitymixDetailPozemek:

    def setup_method(self):
        self.scraper = RealityMixScraper()
        item = {"url": "https://realitymix.cz/detail/horni-dunajovice/lesni-pozemek-8710173.html",
                "external_id": "8710173", "address": "Horní Dunajovice, okr. Znojmo", "price_text": "35 000 Kč"}
        self.d = self.scraper.parse_detail_page(_load("detail_pozemek_8710173.html"), item, "Pozemek", "Prodej", "Znojmo")

    def test_typ_pozemek(self):
        assert self.d["property_type"] == "Pozemek"

    def test_plocha_pozemku_do_area_land(self):
        assert self.d["area_land"] == 406.0
        assert self.d["area_built_up"] is None

    def test_cena(self):
        assert self.d["price"] == 35000.0

    def test_obec_z_adresy_kdyz_breadcrumbs_konci_okresem(self):
        assert self.d["district"] == "Znojmo"
        assert self.d["municipality"] == "Horní Dunajovice"
        assert self.d["location_text"] == "Horní Dunajovice, okres Znojmo"

    def test_gps(self):
        assert self.d["latitude"] == pytest.approx(48.957525)


class TestRealitymixHelpers:

    def test_split_address_s_okresem(self):
        assert RealityMixScraper._split_address("Kloboučky, Zastávka, okr. Brno-venkov") == ("Zastávka", "Brno-venkov")

    def test_split_address_okres_plnym_slovem(self):
        assert RealityMixScraper._split_address("Chvalovice, okres Znojmo") == ("Chvalovice", "Znojmo")

    def test_split_address_bez_okresu(self):
        assert RealityMixScraper._split_address("Ševčenkova, Brno-Bosonohy, Brno") == ("Brno", None)

    @pytest.mark.parametrize("text,expected", [
        ("3 490 000 Kč", 3490000.0), ("442 m²", 442.0), ("39 298 m²", 39298.0), ("dohodou", None), ("", None),
    ])
    def test_parse_number(self, text, expected):
        assert RealityMixScraper._parse_number(text) == expected

    def test_brno_bez_breadcrumbs_mapuje_na_brno_mesto(self):
        html = ('<html><head><meta property="og:title" content="Prodej bytu, 2+kk, Brno"></head><body>'
                '<h1>Prodej bytu, 2+kk, 45 m²</h1><p class="advert-detail-heading__address">Ševčenkova, Brno-Bosonohy, Brno</p>'
                '<span class="advert-detail-heading__price-value">4 000 000 Kč</span></body></html>')
        d = RealityMixScraper().parse_detail_page(html, {"url": "https://realitymix.cz/detail/brno/x-1234567.html",
                                                          "external_id": "1234567"}, "Byt", "Prodej", "Brno-město")
        assert d["district"] == "Brno-město"
        assert d["municipality"] == "Brno"
        assert d["disposition"] == "2+kk"
        assert d["area_built_up"] == 45.0
        assert d["photos"] == []


class TestRealitymixStazenyInzerat:
    """6. 11. 2026: detail staženého inzerátu vrací HTTP 200 s výpisem „podobných nemovitostí“."""

    def test_hlaska_o_stazenem_inzeratu(self):
        assert RealityMixScraper.is_gone_detail(_load("detail_neni_v_databazi_8282543.html")) is True

    @pytest.mark.parametrize("name", [
        "detail_dum_8709111.html", "detail_byt_8689753.html", "detail_pozemek_8710173.html",
        "detail_drazba_bez_fotek_8694091.html", "list_domy_znojmo.html",
    ])
    def test_zivy_detail_ani_vypis_nejsou_stazene(self, name):
        assert RealityMixScraper.is_gone_detail(_load(name)) is False

    def test_stranka_bez_detailu_se_neparsuje(self):
        item = {"url": "https://realitymix.cz/detail/brod-nad-dyji/prodej-rodinneho-domu-64-m-brod-nad-dyji-8282543.html",
                "external_id": "8282543", "address": "Brod nad Dyjí, okr. Břeclav", "price_text": "2 890 000 Kč"}
        # dřív by z výpisu podobných vznikl inzerát s titulkem „Rodinné domy a vily - Břeclav | RealityMIX“
        with pytest.raises(ValueError):
            RealityMixScraper().parse_detail_page(_load("detail_neni_v_databazi_8282543.html"), item,
                                                  "Dům", "Prodej", "Břeclav")


GONE_ID = "8709787"       # detail hlásí „již není v naší databázi“
NOT_FOUND_ID = "8707929"  # detail vrací 404


class _FakeRealityMix(RealityMixScraper):
    """Jeden výpis (fixtura), detaily z fixtur; žádné HTTP, žádná DB."""

    def __init__(self, gone_ids: tuple = (GONE_ID,), not_found_ids: tuple = (NOT_FOUND_ID,)) -> None:
        super().__init__(lists=[("/reality/domy/prodej/jihomoravsky/znojmo", "Dům", "Prodej", "Znojmo")])
        self.gone = set(gone_ids)
        self.not_found = set(not_found_ids)
        self.saved: List[Dict[str, Any]] = []
        self.deactivated: List[str] = []

    async def _fetch(self, url: str) -> str:
        if "/reality/" in url:
            return _load("list_domy_znojmo.html")
        external_id = url.rsplit("-", 1)[1].split(".")[0]
        if external_id in self.not_found:
            request = httpx.Request("GET", url)
            raise httpx.HTTPStatusError("404", request=request, response=httpx.Response(404, request=request))
        if external_id in self.gone:
            return _load("detail_neni_v_databazi_8282543.html")
        return _load("detail_dum_8709111.html")

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        self.saved.append(listing)

    async def _deactivate_listing(self, external_id: str) -> bool:
        self.deactivated.append(external_id)
        return True


@pytest.fixture
def _bez_cekani(monkeypatch):
    async def _no_sleep(_seconds: float) -> None:
        return None

    monkeypatch.setattr(realitymix_scraper.asyncio, "sleep", _no_sleep)


@pytest.mark.usefixtures("_bez_cekani")
class TestRealitymixBehStazene:

    async def test_stazeny_inzerat_se_neulozi_a_deaktivuje(self):
        scraper = _FakeRealityMix()
        total = await scraper.scrape(max_pages=1)
        saved_ids = {listing["external_id"] for listing in scraper.saved}
        assert GONE_ID not in saved_ids and NOT_FOUND_ID not in saved_ids
        assert total == len(scraper.saved) == 19
        assert sorted(scraper.deactivated) == sorted([GONE_ID, NOT_FOUND_ID])

    async def test_bez_stazenych_nic_nedeaktivuje(self):
        scraper = _FakeRealityMix(gone_ids=(), not_found_ids=())
        assert await scraper.scrape(max_pages=1) == 21
        assert scraper.deactivated == []

    async def test_pojistka_kdyz_jsou_stazene_skoro_vsechny(self):
        # 20 z 21 detailů „staženo“ = spíš změna webu než vlna stahování → nedeaktivovat nic
        ids = RealityMixScraper().parse_list_page(_load("list_domy_znojmo.html"))[0]
        scraper = _FakeRealityMix(gone_ids=tuple(i["external_id"] for i in ids[1:]), not_found_ids=())
        assert await scraper.scrape(max_pages=1) == 1
        assert scraper.deactivated == []
