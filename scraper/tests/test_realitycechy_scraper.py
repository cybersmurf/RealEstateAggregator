"""
Unit testy parseru RealityCechy.cz nad uloženými fixturami (tests/fixtures/realitycechy/).
Bez živého HTTP a bez DB.
"""
import sys
from pathlib import Path
from typing import Any, Dict

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.realitycechy_scraper import RealityCechyScraper, DEFAULT_LISTS, DISTRICT_IDS

FIXTURES = Path(__file__).parent / "fixtures" / "realitycechy"


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _item(url: str, external_id: str = "", **extra: Any) -> Dict[str, Any]:
    base = {"external_id": external_id, "url": url, "title": "", "municipality": "",
            "price_text": "", "status": "", "thumb": ""}
    base.update(extra)
    return base


class TestRealityCechyKonfigurace:
    def test_source_code(self):
        assert RealityCechyScraper.SOURCE_CODE == "REALITYCECHY"

    def test_vychozi_seznamy_pokryvaji_tri_okresy_a_tri_typy(self):
        assert len(DEFAULT_LISTS) == 9
        assert {d for _, _, _, d, _ in DEFAULT_LISTS} == {"Znojmo", "Brno-venkov", "Brno-město"}
        assert {p for _, p, _, _, _ in DEFAULT_LISTS} == {"Dům", "Byt", "Pozemek"}
        assert all(o == "Prodej" for _, _, o, _, _ in DEFAULT_LISTS)

    def test_url_vypisu_prvni_strana(self):
        url = RealityCechyScraper.build_list_url("/nemovitosti/prodej-domu/", DISTRICT_IDS["Znojmo"])
        assert url == "https://www.realitycechy.cz/nemovitosti/prodej-domu/?okresy_id%5B0%5D=20023713"

    def test_url_vypisu_dalsi_strana(self):
        url = RealityCechyScraper.build_list_url("/nemovitosti/prodej-bytu/", DISTRICT_IDS["Brno-venkov"], page=3)
        assert url == "https://www.realitycechy.cz/nemovitosti/prodej-bytu/?vp-page=3&okresy_id%5B0%5D=20023703"


class TestRealityCechyVypis:
    def setup_method(self):
        self.scraper = RealityCechyScraper()
        self.items, self.total, self.has_next = self.scraper.parse_list_page(_load("list_domy_znojmo.html"), page=1)

    def test_pocet_polozek_na_strance(self):
        assert len(self.items) == 24

    def test_celkovy_pocet_a_dalsi_strana(self):
        assert self.total == 170
        assert self.has_next is True

    def test_external_id_a_url(self):
        first = self.items[0]
        assert first["external_id"] == "21205230"
        assert first["url"] == ("https://www.realitycechy.cz/nemovitost/prodej/"
                                "moderni-rodinny-dum-s-velkou-zahradou-vyber-nejvyhodnejsi-nabidky/21205230")

    def test_external_id_jsou_unikatni_a_ciselna(self):
        ids = [i["external_id"] for i in self.items]
        assert len(set(ids)) == 24
        assert all(i.isdigit() for i in ids)

    def test_obec_bez_prefixu_ulice(self):
        assert self.items[0]["municipality"] == "Dobšice"
        assert not any(i["municipality"].lower().startswith("ulice") for i in self.items)

    def test_cena_a_nahled(self):
        assert self.scraper._parse_number(self.items[0]["price_text"]) == 10_000_000
        assert self.items[0]["thumb"].startswith("https://www.realitycechy.cz/foto/mq/")

    def test_nadpis_bez_rozdeleneho_m2(self):
        assert "pozemkem 2 336 m2 v Rozkoši" in self.items[1]["title"]

    def test_zaloha_ld_json_kdyz_chybi_karty(self):
        html = _load("list_domy_znojmo.html").replace('class="nem-item"', 'class="x-item"')
        items, _, _ = self.scraper.parse_list_page(html, page=1)
        assert len(items) == 24
        assert items[0]["external_id"] == "21205230"


class TestRealityCechyDetailDum:
    def setup_method(self):
        self.scraper = RealityCechyScraper()
        item = _item("https://www.realitycechy.cz/nemovitost/prodej/rodinny-dum-3-kk-v-sebranicich/21205470", "21205470")
        self.d = self.scraper.parse_detail_page(_load("detail_dum.html"), item, "Dům", "Prodej", "Znojmo")

    def test_zakladni_pole(self):
        assert self.d["source_code"] == "REALITYCECHY"
        assert self.d["external_id"] == "21205470"
        assert self.d["title"] == "Rodinný dům 3+kk v Sebranicích – útulné bydlení bez starostí"
        assert self.d["property_type"] == "Dům"
        assert self.d["offer_type"] == "Prodej"

    def test_cena(self):
        assert self.d["price"] == 1_690_000

    def test_plochy(self):
        assert self.d["area_built_up"] == 85
        assert self.d["area_land"] == 93

    def test_okres_z_detailu_prebiji_okres_vypisu(self):
        assert self.d["district"] == "Blansko"
        assert self.d["municipality"] == "Sebranice"
        assert self.d["location_text"] == "Sebranice, okres Blansko"

    def test_dispozice_stav_konstrukce(self):
        assert self.d["disposition"] == "3+kk"
        assert self.d["rooms"] == 3
        assert self.d["condition"] == "Dobrý"

    def test_fotky_v_plne_velikosti(self):
        assert len(self.d["photos"]) == 23
        assert self.d["photos"][0] == "https://www.realitycechy.cz/foto/hq/21205000/21205470/21205470_01.webp"
        assert len(set(self.d["photos"])) == 23

    def test_gps(self):
        assert self.d["latitude"] == pytest.approx(49.4983304)
        assert self.d["longitude"] == pytest.approx(16.5727867)

    def test_popis_a_realitka(self):
        assert self.d["description"].startswith("Hledáte klidné a praktické bydlení")
        assert "85 m2" in self.d["description"]
        assert "Realitní kancelář: Nejlepsireality.cz" in self.d["description"]
        assert "Status: rezervováno" in self.d["description"]
        assert "spočítat hypotéku" not in self.d["description"]


class TestRealityCechyDetailByt:
    def setup_method(self):
        self.scraper = RealityCechyScraper()
        item = _item("https://www.realitycechy.cz/nemovitost/prodej/byt-3-1-ve-valticich/21205243", "21205243")
        self.d = self.scraper.parse_detail_page(_load("detail_byt.html"), item, "Byt", "Prodej", "Brno-město")

    def test_typ_a_dispozice(self):
        assert self.d["property_type"] == "Byt"
        assert self.d["disposition"] == "3+1"
        assert self.d["rooms"] == 3

    def test_cena_a_plocha(self):
        assert self.d["price"] == 4_499_000
        assert self.d["area_built_up"] == 67
        assert self.d["area_land"] is None

    def test_adresa_s_ulici(self):
        assert self.d["district"] == "Břeclav"
        assert self.d["municipality"] == "Valtice"
        assert self.d["location_text"] == "Lázeňská, Valtice, okres Břeclav"

    def test_konstrukce_panel(self):
        assert self.d["construction_type"] == "Panel"

    def test_fotky(self):
        assert len(self.d["photos"]) == 14


class TestRealityCechyDetailPozemek:
    def setup_method(self):
        self.scraper = RealityCechyScraper()
        item = _item("https://www.realitycechy.cz/nemovitost/prodej/pozemek-ivancice/21205223", "21205223")
        self.d = self.scraper.parse_detail_page(_load("detail_pozemek.html"), item, "Pozemek", "Prodej", "Brno-venkov")

    def test_typ_pozemek(self):
        assert self.d["property_type"] == "Pozemek"
        assert self.d["area_built_up"] is None

    def test_okres_brno_venkov_normalizovany(self):
        assert self.d["district"] == "Brno-venkov"
        assert self.d["location_text"] == "Ivančice, okres Brno-venkov"

    def test_cena_a_neuvedene_hodnoty(self):
        assert self.d["price"] == 3_490_000
        assert self.d["condition"] is None
        assert self.d["construction_type"] is None
        assert self.d["disposition"] is None

    def test_fotky(self):
        assert len(self.d["photos"]) == 10
        assert all(p.startswith("https://www.realitycechy.cz/foto/hq/") for p in self.d["photos"])


class TestRealityCechyDetailChata:
    def setup_method(self):
        self.scraper = RealityCechyScraper()
        item = _item("https://www.realitycechy.cz/nemovitost/prodej/krhovice-chata/21198956", "21198956")
        self.d = self.scraper.parse_detail_page(_load("detail_chata.html"), item, "Dům", "Prodej", "Znojmo")

    def test_druh_chaty_prebiji_typ_vypisu(self):
        assert self.d["property_type"] == "Chata"

    def test_okres_znojmo(self):
        assert self.d["district"] == "Znojmo"
        assert self.d["municipality"] == "Krhovice"

    def test_cena_plochy_konstrukce(self):
        assert self.d["price"] == 2_655_485
        assert self.d["area_built_up"] == 70
        assert self.d["area_land"] == 70
        assert self.d["construction_type"] == "Cihla"
        assert self.d["disposition"] == "1+1"


class TestRealityCechyPomocneMetody:
    def setup_method(self):
        self.scraper = RealityCechyScraper()

    @pytest.mark.parametrize("text,expected", [
        ("1 690 000 Kč", 1_690_000),
        ("4\xa0499\xa0000\xa0Kč spočítat hypotéku", 4_499_000),
        ("75.3 m2", 75.3),
        ("75,3 m2", 75.3),
        ("cena na vyžádání", None),
        ("", None),
        (None, None),
    ])
    def test_parse_number(self, text, expected):
        assert self.scraper._parse_number(text) == expected

    @pytest.mark.parametrize("text,expected", [
        ("Brno - venkov", "Brno-venkov"),
        ("Brno - město", "Brno-město"),
        ("Znojmo", "Znojmo"),
    ])
    def test_normalize_district(self, text, expected):
        assert self.scraper._normalize_district(text) == expected

    @pytest.mark.parametrize("kind,default,expected", [
        ("rodinný dům", "Dům", "Dům"),
        ("chaty a chalupy", "Dům", "Chata"),
        ("byt 3+1", "Byt", "Byt"),
        ("stavební - bydlení", "Pozemek", "Pozemek"),
        ("", "Pozemek", "Pozemek"),
    ])
    def test_kind_to_property_type(self, kind, default, expected):
        assert self.scraper._kind_to_property_type(kind, default) == expected

    @pytest.mark.parametrize("text,expected", [
        ("ulice Čechova, Břeclav", "Břeclav"),
        ("Sebranice", "Sebranice"),
        ("  ulice třída Komenského,  Kyjov ", "Kyjov"),
    ])
    def test_clean_municipality(self, text, expected):
        assert self.scraper._clean_municipality(text) == expected
