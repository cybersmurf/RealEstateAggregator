"""
Unit testy parseru RealcityScraper nad uloženými fixturami (tests/fixtures/realcity).
Bez HTTP a bez DB.
"""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.realcity_scraper import RealcityScraper, DEFAULT_LISTS, PER_PAGE

FIXTURES = Path(__file__).parent / "fixtures" / "realcity"


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _item(external_id: str, slug: str) -> dict:
    return {"external_id": external_id, "url": f"https://www.realcity.cz/nemovitost/{slug}-{external_id}",
            "title": "", "municipality": "", "price_text": "", "short_description": "", "thumb": None}


class TestRealcityVypis:
    def setup_method(self):
        self.scraper = RealcityScraper()
        self.items, self.total, self.has_next = self.scraper.parse_list_page(_load("list_domy_znojmo.html"))

    def test_pocet_polozek_a_celkem(self):
        assert len(self.items) == 40
        assert self.total == 40
        assert self.has_next is False

    def test_external_id_a_url(self):
        ids = {i["external_id"] for i in self.items}
        assert "4404079" in ids
        item = next(i for i in self.items if i["external_id"] == "4404079")
        assert item["url"] == "https://www.realcity.cz/nemovitost/prodej-domu-suchohrdly-skolni-396-4404079"
        assert all(i["url"].startswith("https://www.realcity.cz/nemovitost/") for i in self.items)

    def test_cena_obec_a_nahled(self):
        item = next(i for i in self.items if i["external_id"] == "4404079")
        assert item["price_text"] == "dohodou"
        assert item["municipality"] == "Suchohrdly, Školní 396"
        assert item["thumb"].startswith("https://media.realcity.cz/files/")
        priced = [i for i in self.items if "Kč" in i["price_text"]]
        assert len(priced) > 30

    def test_dalsi_stranka_z_link_rel_next(self):
        items, total, has_next = self.scraper.parse_list_page(_load("list_domy_brno_venkov_p1.html"))
        assert len(items) == 20
        assert total == 37
        assert has_next is True

    def test_url_vypisu(self):
        assert RealcityScraper.list_url("/prodej-domu/znojmo-79") == \
            f"https://www.realcity.cz/prodej-domu/znojmo-79?list-perPage={PER_PAGE}&list-sort=updated-desc"
        assert "list-page=3" in RealcityScraper.list_url("/prodej-domu/znojmo-79", 3)

    def test_vychozi_vypisy_pokryvaji_tri_okresy(self):
        assert {d for _, _, _, d in DEFAULT_LISTS} == {"Znojmo", "Brno-venkov", "Brno-město"}
        assert {t for _, t, _, _ in DEFAULT_LISTS} == {"Dům", "Byt", "Pozemek"}
        assert all(o == "Prodej" for _, _, o, _ in DEFAULT_LISTS)


class TestRealcityDetailDum:
    def setup_method(self):
        self.scraper = RealcityScraper()
        self.d = self.scraper.parse_detail_page(
            _load("detail_dum_suchohrdly_4404079.html"),
            _item("4404079", "prodej-domu-suchohrdly-skolni-396"), "Dům", "Prodej", "Znojmo")

    def test_zakladni_pole(self):
        assert self.d["source_code"] == "REALCITY"
        assert self.d["external_id"] == "4404079"
        assert self.d["url"].endswith("/nemovitost/prodej-domu-suchohrdly-skolni-396-4404079")
        assert self.d["title"] == "Prodej domu 146 m², Suchohrdly, Školní 396"
        assert self.d["property_type"] == "Dům"
        assert self.d["offer_type"] == "Prodej"

    def test_cena_dohodou_je_none(self):
        assert self.d["price"] is None

    def test_plochy(self):
        assert self.d["area_built_up"] == 146.0
        assert self.d["area_land"] == 384.0

    def test_okres_a_obec_z_breadcrumbs(self):
        assert self.d["district"] == "Znojmo"
        assert self.d["municipality"] == "Suchohrdly"
        assert self.d["location_text"] == "Suchohrdly, Školní 396, okres Znojmo"

    def test_gps_z_mapy(self):
        assert self.d["latitude"] == pytest.approx(48.866159)
        assert self.d["longitude"] == pytest.approx(16.089647)

    def test_fotky(self):
        assert len(self.d["photos"]) == 28
        assert self.d["photos"][0] == "https://media.realcity.cz/files/resized/2026/06/195086/img_6a39aaff30e559.21902886.jpg"
        assert len(set(self.d["photos"])) == 28

    def test_stav_konstrukce_popis(self):
        assert self.d["condition"] == "novostavba"
        assert self.d["construction_type"] == "cihlová"
        assert self.d["description"].startswith("Nabízíme k prodeji nemovitost z roku 2014")
        assert "Realitní kancelář: Swiss Life Select Reality" in self.d["description"]
        assert len(self.d["description"]) <= 5000


class TestRealcityDetailOstatniTypy:
    def setup_method(self):
        self.scraper = RealcityScraper()

    def test_dum_s_cenou_okres_brno_venkov(self):
        d = self.scraper.parse_detail_page(_load("detail_dum_deblin_4420717.html"),
                                           _item("4420717", "prodej-domu-deblin"), "Dům", "Prodej", "Brno-venkov")
        assert d["price"] == 6900000.0
        assert d["district"] == "Brno-venkov"
        assert d["municipality"] == "Deblín"
        assert d["area_built_up"] == 95.0
        assert d["area_land"] == 696.0
        assert d["construction_type"] == "smíšená"
        assert d["condition"] == "dobrý stav"
        assert "latitude" not in d

    def test_byt_dispozice_a_pokoje(self):
        d = self.scraper.parse_detail_page(_load("detail_byt_hrusovany_4421934.html"),
                                           _item("4421934", "prodej-bytu-2-1-hrusovany-nad-jevisovkou"), "Byt", "Prodej", "Znojmo")
        assert d["property_type"] == "Byt"
        assert d["price"] == 3493280.0
        assert d["disposition"] == "2+1"
        assert d["rooms"] == 2
        assert d["area_built_up"] == 49.0
        assert d["area_land"] is None
        assert d["municipality"] == "Hrušovany nad Jevišovkou"
        assert d["district"] == "Znojmo"
        assert len(d["photos"]) == 11

    def test_pozemek_ma_jen_plochu_pozemku(self):
        d = self.scraper.parse_detail_page(_load("detail_pozemek_blizkovice_4420047.html"),
                                           _item("4420047", "prodej-pozemku-blizkovice"), "Pozemek", "Prodej", "Znojmo")
        assert d["property_type"] == "Pozemek"
        assert d["price"] == 1929042.0
        assert d["area_land"] == 726.0
        assert d["area_built_up"] is None
        assert d["rooms"] is None
        assert d["municipality"] == "Blížkovice"
        assert d["location_text"] == "Blížkovice, okres Znojmo"
        assert len(d["photos"]) == 8

    def test_typ_z_breadcrumbs_prebiji_parametr(self):
        d = self.scraper.parse_detail_page(_load("detail_byt_hrusovany_4421934.html"),
                                           _item("4421934", "prodej-bytu-2-1-hrusovany-nad-jevisovkou"), "Dům", "Prodej", "Brno-město")
        assert d["property_type"] == "Byt"
        assert d["district"] == "Znojmo"

    def test_prazdne_html_nespadne(self):
        d = self.scraper.parse_detail_page("<html><body></body></html>",
                                           {**_item("1", "x"), "title": "Dům prodej", "price_text": "1 500 000 Kč",
                                            "municipality": "Miroslav", "thumb": "https://media.realcity.cz/x.jpg"},
                                           "Dům", "Prodej", "Znojmo")
        assert d["title"] == "Dům prodej"
        assert d["price"] == 1500000.0
        assert d["municipality"] == "Miroslav"
        assert d["district"] == "Znojmo"
        assert d["photos"] == ["https://media.realcity.cz/x.jpg"]
        items, total, has_next = self.scraper.parse_list_page("<html></html>")
        assert items == [] and total == 0 and has_next is False

    def test_datum_zverejneni_z_datalayer(self):
        d = self.scraper.parse_detail_page(_load("detail_dum_suchohrdly_4404079.html"),
                                           _item("4404079", "prodej-domu-suchohrdly-skolni-396"), "Dům", "Prodej", "Znojmo")
        assert d["date_created_source"].isoformat() == "2026-06-22T23:06:51+02:00"
