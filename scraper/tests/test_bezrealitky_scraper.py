"""
Unit testy pro BezrealitkyScraper – parsování uložených odpovědí GraphQL API
a SSR detailu (tests/fixtures/bezrealitky/), bez HTTP a DB.
"""
import json
import sys
from pathlib import Path
from typing import Any, Dict

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.bezrealitky_scraper import BezrealitkyScraper, BASE_URL

FIXTURES = Path(__file__).parent / "fixtures" / "bezrealitky"


def _load(name: str) -> Dict[str, Any]:
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


class TestBezrealitkyVypis:

    def setup_method(self):
        self.scraper = BezrealitkyScraper()
        self.adverts, self.total = self.scraper.parse_list_response(_load("list_znojmo_dum.json"))
        self.first = self.scraper.normalize_advert(self.adverts[0], "Dům", "Prodej", "Znojmo")

    def test_pocet_polozek_a_total(self):
        assert len(self.adverts) == 3
        assert self.total == 3

    def test_external_id_a_url(self):
        assert self.first["external_id"] == "1050010"
        assert self.first["url"] == f"{BASE_URL}/nemovitosti-byty-domy/1050010-nabidka-prodej-domu-krhovice"
        assert self.first["source_code"] == "BEZREALITKY"

    def test_cena(self):
        assert self.first["price"] == 11900000.0
        assert self.first["offer_type"] == "Prodej"

    def test_plochy(self):
        assert self.first["area_built_up"] == 156.0
        assert self.first["area_land"] == 768.0

    def test_okres_a_obec(self):
        assert self.first["district"] == "Znojmo"
        assert self.first["municipality"] == "Krhovice"
        assert self.first["location_text"] == "Krhovice, okres Znojmo"

    def test_typ_nemovitosti(self):
        assert self.first["property_type"] == "Dům"

    def test_dispozice_a_pokoje(self):
        assert self.first["disposition"] == "4+kk"
        assert self.first["rooms"] == 4

    def test_stav_a_konstrukce(self):
        assert self.first["condition"] == "Novostavba"
        assert self.first["construction_type"] == "Dřevostavba"

    def test_fotky(self):
        assert len(self.first["photos"]) == 6
        assert all(p.startswith("https://api.bezrealitky.cz/media/cache/record_main/") for p in self.first["photos"])
        assert self.first["photos"][0].endswith("titulka-krhovice.jpg")

    def test_titulek_obsahuje_obec(self):
        assert self.first["title"] == "Prodej domu 156 m², pozemek 768 m², Krhovice"

    def test_popis_a_gps(self):
        assert self.first["description"].startswith("Novostavba 4+kk")
        assert self.first["latitude"] == pytest.approx(48.8200833)
        assert self.first["longitude"] == pytest.approx(16.1783838)

    def test_obec_s_odlisnou_casti(self):
        tasovice = self.scraper.normalize_advert(self.adverts[1], "Dům", "Prodej", "Znojmo")
        assert tasovice["municipality"] == "Tasovice"
        assert tasovice["location_text"] == "Tasovice, okres Znojmo"
        assert tasovice["condition"] == "Novostavba"

    def test_okres_ze_stromu_ma_prednost_pred_fallbackem(self):
        listing = self.scraper.normalize_advert(self.adverts[2], "Dům", "Prodej", "Břeclav")
        assert listing["district"] == "Znojmo"
        assert listing["condition"] == "Dobrý"
        assert listing["disposition"] == "5+kk"


class TestBezrealitkyByty:

    def setup_method(self):
        self.scraper = BezrealitkyScraper()
        adverts, self.total = self.scraper.parse_list_response(_load("list_brno_venkov_byt.json"))
        self.items = [self.scraper.normalize_advert(a, "Byt", "Prodej", "Brno-venkov") for a in adverts]

    def test_total_a_pocet(self):
        assert self.total == 8
        assert len(self.items) == 3

    def test_byt_bez_pozemku(self):
        first = self.items[0]
        assert first["property_type"] == "Byt"
        assert first["area_built_up"] == 62.0
        assert first["area_land"] is None
        assert first["disposition"] == "3+kk"
        assert first["rooms"] == 3

    def test_okres_brno_venkov(self):
        assert {i["district"] for i in self.items} == {"Brno-venkov"}
        assert self.items[0]["municipality"] == "Šlapanice"
        assert self.items[0]["location_text"] == "Šlapanice, okres Brno-venkov"

    def test_stav_projekt(self):
        assert self.items[1]["condition"] == "Projekt"
        assert self.items[1]["price"] == 5390000.0


class TestBezrealitkyPozemky:

    def setup_method(self):
        self.scraper = BezrealitkyScraper()
        adverts, self.total = self.scraper.parse_list_response(_load("list_brno_mesto_pozemek.json"))
        self.items = [self.scraper.normalize_advert(a, "Pozemek", "Prodej", "Brno-město") for a in adverts]

    def test_total(self):
        assert self.total == 17

    def test_pozemek_ma_jen_plochu_pozemku(self):
        first = self.items[0]
        assert first["property_type"] == "Pozemek"
        assert first["area_built_up"] is None
        assert first["area_land"] == 4579.0
        assert first["disposition"] is None
        assert first["rooms"] is None
        assert first["condition"] is None

    def test_brno_mestska_cast(self):
        first = self.items[0]
        assert first["district"] == "Brno-město"
        assert first["municipality"] == "Brno"
        assert first["location_text"] == "Brno - Chrlice, okres Brno-město"

    def test_import_je_oznacen_v_popisu(self):
        assert "Import na Bezrealitky: INVESTUJ_DO_POLE" in self.items[0]["description"]
        assert self.items[0]["price"] == 750000.0


class TestBezrealitkyDetail:

    def setup_method(self):
        self.scraper = BezrealitkyScraper()
        html = (FIXTURES / "detail_1069510.html").read_text(encoding="utf-8")
        self.detail = self.scraper.parse_detail_page(html, "1069510")

    def test_popis(self):
        assert self.detail["description"].startswith("Nabízíme k prodeji moderní rodinný dům o dispozici 4+kk")

    def test_fotky_record_main(self):
        assert len(self.detail["photos"]) == 4
        assert self.detail["photos"][0].startswith("https://api.bezrealitky.cz/media/cache/record_main/")
        assert len(set(self.detail["photos"])) == 4

    def test_gps(self):
        assert self.detail["latitude"] == pytest.approx(48.8358764, abs=1e-5)
        assert self.detail["longitude"] == pytest.approx(16.158812, abs=1e-5)

    def test_bez_next_data_vyhodi_chybu(self):
        with pytest.raises(ValueError):
            self.scraper.parse_detail_page("<html><body>nic</body></html>", "1")


class TestBezrealitkyPomocne:

    @pytest.mark.parametrize("code,expected", [
        ("DISP_1_KK", ("1+kk", 1)),
        ("DISP_3_1", ("3+1", 3)),
        ("DISP_7_IZB", ("7+izb", 7)),
        ("GARSONIERA", ("Garsoniéra", 1)),
        ("UNDEFINED", (None, None)),
        ("OSTATNI", (None, None)),
        (None, (None, None)),
    ])
    def test_dispozice(self, code, expected):
        assert BezrealitkyScraper._disposition(code) == expected

    def test_okres_ze_stromu(self):
        tree = [{"lvl": 2, "name": "Jihomoravský kraj"}, {"lvl": 3, "name": "okres Brno-město"}, {"lvl": 4, "name": "Brno"}]
        assert BezrealitkyScraper._district_from_tree(tree) == "Brno-město"
        assert BezrealitkyScraper._district_from_tree([]) is None
        assert BezrealitkyScraper._district_from_tree(None) is None

    def test_prazdna_odpoved(self):
        adverts, total = BezrealitkyScraper.parse_list_response({"data": {"listAdverts": {"totalCount": 0, "list": []}}})
        assert adverts == []
        assert total == 0
