"""PREMIA Reality – okres ze slugu URL, obec z tabulky a typ z „Podtyp nemovitosti".

6. 10. 2026: z 8 volných domů na Znojemsku prošly geografickým filtrem jen 2 (ty se „Znojmo"
v adrese) – scraper posílal jako lokalitu jen název obce a okres vůbec. Zahrady z /rekreace/
se ukládaly jako domy.
"""
from pathlib import Path

import pytest

from core.filters import FilterManager
from core.scrapers.premiareality_scraper import PremiaRealityScraper

FIX = Path(__file__).parent / "fixtures" / "premiareality"
BASE = "https://www.premiareality.cz"


def _parse(fixture: str, path: str, default_type: str = "Dům") -> dict:
    html = (FIX / fixture).read_text(encoding="utf-8")
    item = {"url": BASE + path, "title": "", "default_property_type": default_type}
    return PremiaRealityScraper()._parse_detail_page(html, item)


class TestPremiaOkresZeSlugu:
    @pytest.mark.parametrize("path,expected", [
        ("/domy/prodej-rodinneho-domu-strachotice-okres-znojmo-dum-3-1-na-prodej-3164.html", "Znojmo"),
        ("/domy/prodej-rodinneho-domu-troskotovice-okres-brno-venkov-prodej-domu-4-kk-3158.html", "Brno-venkov"),
        ("/byty/prodej-bytu-4-kk-brno-okres-brno-mesto-byt-4-kk-na-prodej-3102.html", "Brno-město"),
        ("/domy/prodej-chalupy-pristpo-okres-trebic-dum-na-prodej-3147.html", "Třebíč"),
        ("/parcely/prodej-stavebniho-pozemku-trpik-okres-usti-nad-orlici-prodej-pozemku-v-obci-1.html", "Ústí nad Orlicí"),
        ("/parcely/prodej-specifickeho-pozemku-svratka-okres-zdar-nad-sazavou-prodej-pozemku-2.html", "Žďár nad Sázavou"),
    ])
    def test_okres_se_pozna_ze_slugu(self, path, expected):
        assert PremiaRealityScraper._district_from_url(BASE + path) == expected

    def test_nabidka_bez_okresu_ve_slugu_nema_okres(self):
        url = BASE + "/byty/prodej-bytu-3-kk-praha-liben-byt-3-kk-na-prodej-3117.html"
        assert PremiaRealityScraper._district_from_url(url) is None

    def test_neznamy_okres_neni_zadny_z_cilovych(self):
        url = BASE + "/ostatni/prodej-specificke-nemovitosti-peruc-okres-kolin-drazni-domek-3121.html"
        assert PremiaRealityScraper._district_from_url(url) == "Kolin"


class TestPremiaLokalita:
    def test_dum_na_vesnici_ma_obec_a_okres(self):
        listing = _parse("detail_dum_strachotice_3164.html",
                         "/domy/prodej-rodinneho-domu-strachotice-okres-znojmo-dum-3-1-na-prodej-3164.html")
        assert listing["district"] == "Znojmo"
        assert listing["municipality"] == "Strachotice"
        assert listing["location_text"] == "Strachotice"
        assert listing["property_type"] == "Dům"
        assert listing["price"] == 2_190_000

    def test_dum_na_vesnici_projde_geografickym_filtrem(self):
        listing = _parse("detail_dum_strachotice_3164.html",
                         "/domy/prodej-rodinneho-domu-strachotice-okres-znojmo-dum-3-1-na-prodej-3164.html")
        assert FilterManager().passes_search_filters(listing) is True

    def test_nabidka_s_ulici_ma_ulici_i_mesto(self):
        listing = _parse("detail_dum_ulice_znojmo_3106.html",
                         "/domy/prodej-rodinneho-domu-znojmo-okres-znojmo-dum-3-1-na-prodej-3106.html")
        assert listing["location_text"] == "Koželužská, Znojmo"
        assert listing["municipality"] == "Znojmo"
        assert listing["district"] == "Znojmo"

    def test_rezervovany_dum_na_vesnici_ma_okres_i_stitek(self):
        listing = _parse("detail_dum_rezervace_zeletice_3150.html",
                         "/domy/prodej-rodinneho-domu-zeletice-okres-znojmo-prodej-domu-4-1-3150.html")
        assert listing["district"] == "Znojmo"
        assert listing["municipality"] == "Želetice"
        assert listing["price"] is None
        assert listing["price_note"] == "Rezervace"
        assert listing["keep_last_price"] is True

    def test_bez_tabulky_se_obec_vezme_z_podtitulku(self):
        html = "<html><body><h1>Dům na prodej</h1><h2>Na Hrázi - Znojmo</h2></body></html>"
        item = {"url": BASE + "/domy/prodej-rodinneho-domu-znojmo-okres-znojmo-dum-5-1-na-prodej-3043.html", "title": ""}
        listing = PremiaRealityScraper()._parse_detail_page(html, item)
        assert listing["location_text"] == "Na Hrázi, Znojmo"
        assert listing["municipality"] == "Znojmo"


class TestPremiaTypNemovitosti:
    def test_zahrada_z_rekreace_neni_dum(self):
        # Výchozí typ schválně „Dům" – tak se zahrady ukládaly před opravou
        listing = _parse("detail_zahrada_rekreace_3066.html",
                         "/rekreace/prodej-zahrady-znojmo-okres-znojmo-zahrada-na-prodej-3066.html", "Dům")
        assert listing["property_type"] == "Pozemek"
        assert listing["area_land"] == 929
        assert listing["price"] == 1_799_000

    def test_komercni_pozemek_je_pozemek_s_cenou_na_dotaz(self):
        listing = _parse("detail_pozemek_komercni_2318.html",
                         "/parcely/prodej-komercniho-pozemku-znojmo-okres-znojmo-pozemek-na-prodej-2318.html", "Pozemek")
        assert listing["property_type"] == "Pozemek"
        assert listing["price"] is None
        assert listing["price_note"].startswith("Informace o ceně v RK")

    def test_kancelar_k_pronajmu_je_komercni(self):
        listing = _parse("detail_kancelar_pronajem_3074.html",
                         "/ostatni/pronajem-kancelare-znojmo-okres-znojmo-komercni-prostor-k-pronajmu-3074.html", "Ostatní")
        assert listing["property_type"] == "Komerční"
        assert listing["offer_type"] == "Pronájem"
        assert listing["price"] == 10_000
        assert listing["location_text"] == "Horní náměstí, Znojmo"

    def test_byt_ma_typ_v_radku_typ_nemovitosti(self):
        listing = _parse("detail_byt_rezervace_3119.html",
                         "/byty/prodej-bytu-2-1-znojmo-okres-znojmo-byt-2-1-na-prodej-3119.html", "Ostatní")
        assert listing["property_type"] == "Byt"
        assert listing["price_note"] == "Rezervace"

    @pytest.mark.parametrize("params,category,expected", [
        ({"nemovitost": "Dům"}, "domy", "Dům"),
        ({"typ nemovitosti": "Byt"}, "byty", "Byt"),
        ({"podtyp nemovitosti": "Zahrada"}, "rekreace", "Pozemek"),
        ({"podtyp nemovitosti": "Sady / Vinice"}, "parcely", "Pozemek"),
        ({"podtyp nemovitosti": "Bydlení"}, "parcely", "Pozemek"),
        ({"podtyp nemovitosti": "Komerční"}, "parcely", "Pozemek"),
        ({"podtyp nemovitosti": "Kanceláře"}, "ostatni", "Komerční"),
        ({"podtyp nemovitosti": "Výroba"}, "ostatni", "Komerční"),
        ({"podtyp nemovitosti": "Sklady"}, "ostatni", "Komerční"),
        ({"podtyp nemovitosti": "Garáž"}, "ostatni", "Garáž"),
        ({"podtyp nemovitosti": "Vinný sklep"}, "ostatni", "Ostatní"),
    ])
    def test_typ_podle_popisku_a_kategorie(self, params, category, expected):
        url = f"{BASE}/{category}/prodej-x-znojmo-okres-znojmo-x-1.html"
        assert PremiaRealityScraper._property_type(params, url, "Ostatní") == expected

    def test_bez_popisku_plati_vychozi_typ_kategorie(self):
        url = f"{BASE}/rekreace/prodej-x-znojmo-okres-znojmo-x-1.html"
        assert PremiaRealityScraper._property_type({}, url, "Pozemek") == "Pozemek"
