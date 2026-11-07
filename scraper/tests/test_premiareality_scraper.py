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


TVORIHRAZ = "/domy/prodej-rodinneho-domu-tvorihraz-okres-znojmo-dum-na-prodej-3124.html"


class TestPremiaCenaAFotkyJenZNabidky:
    """6. 11. 2026: stránka nese i blok „Podobné nabídky" s cenami a náhledy cizích domů. Náhledy
    306×172 se ukládaly jako fotky nabídky (stejné u až deseti nabídek); cena musí být z tabulky
    „Základní přehled"."""

    def test_cena_ze_zakladniho_prehledu_ne_z_podobnych_nabidek(self):
        html = (FIX / "detail_dum_tvorihraz_sleva_3124.html").read_text(encoding="utf-8")
        assert "Podobné nabídky" in html and "2\xa0999\xa0000\xa0Kč" in html
        listing = _parse("detail_dum_tvorihraz_sleva_3124.html", TVORIHRAZ)
        assert listing["price"] == 3_190_000
        assert "price_note" not in listing
        assert listing["area_built_up"] == 100
        assert listing["area_land"] == 135
        assert listing["municipality"] == "Tvořihráz"

    def test_fotky_jen_z_galerie_nabidky_v_plne_velikosti(self):
        listing = _parse("detail_dum_tvorihraz_sleva_3124.html", TVORIHRAZ)
        photos = listing["photos"]
        assert len(photos) == 4  # náhled nahoře je první fotka galerie – nezdvojí se
        # Originály (1756×1170) místo náhledů galerie 1300×866 – kvůli klasifikaci (7. 11. 2026)
        assert all(url.startswith("https://www.premiareality.cz/files/importestate/3124/1920x1920wm") for url in photos)
        assert not any("thumbs_" in url or "_thumb" in url for url in photos)
        assert not any("/3106/" in url or "/3170/" in url for url in photos)

    def test_bez_nadpisu_se_nebere_tabulka_z_podobnych_nabidek(self):
        html = """<html><body><h1>Dům na prodej</h1><h2>Tvořihráz</h2>
        <section id="nemovitosti"><table>
          <tr><td class="prodano" colspan="2">PRODÁNO</td></tr>
          <tr><td>Cena</td><td><strong>2 999 000 Kč</strong></td></tr>
          <tr><td>Užitná plocha</td><td><strong>110 m<sup>2</sup></strong></td></tr>
        </table>
        <img src="https://www.premiareality.cz/files/importestate/3106/thumbs_306_172/a_thumb306x172.jpg"></section>
        <table>
          <tr><td class="price">Cena</td><td><strong>3 190 000 Kč</strong><br/>+ provize RK 3 %</td></tr>
          <tr><td>Užitná plocha</td><td><strong>100 m<sup>2</sup></strong></td></tr>
          <tr><td>Užitná plocha</td><td><strong>999 m<sup>2</sup></strong></td></tr>
        </table>
        <img src="https://www.premiareality.cz/files/importestate/3124/thumbs_1300_731/b_thumb1300x731.jpg">
        </body></html>"""
        listing = PremiaRealityScraper()._parse_detail_page(
            html, {"url": BASE + TVORIHRAZ, "title": "", "default_property_type": "Dům"}
        )
        assert "sold" not in listing  # štítek PRODÁNO patří podobné nabídce
        assert listing["price"] == 3_190_000
        assert listing["area_built_up"] == 100  # první výskyt popisku platí
        assert listing["photos"] == ["https://www.premiareality.cz/files/importestate/3124/b.jpg"]

    @pytest.mark.parametrize("text, expected", [
        ("3\xa0190\xa0000\xa0Kč + provize RK", 3_190_000),
        ("3 190 000 Kč + provize RK 3 %", 3_190_000),
        ("1\xa0196\xa0202\xa0Kč", 1_196_202),
        ("7 500 Kč", 7_500),
        ("1 250 000,50 Kč", 1_250_000),
        ("Informace o ceně v RK", None),
        ("", None),
    ])
    def test_castka_z_bunky_cena(self, text, expected):
        assert PremiaRealityScraper()._parse_price(text) == expected


@pytest.mark.parametrize("thumb, original", [
    ("https://www.premiareality.cz/files/importestate/3124/thumbs_1300_866/1920x1920wm-x-2751154822_thumb1300x866.jpg",
     "https://www.premiareality.cz/files/importestate/3124/1920x1920wm-x-2751154822.jpg"),
    ("https://www.premiareality.cz/files/importestate/3151/thumbs_1300_731/zahrada-90-jpeg-33a6_thumb1300x731.jpg",
     "https://www.premiareality.cz/files/importestate/3151/zahrada-90-jpeg-33a6.jpg"),
    ("https://www.premiareality.cz/files/importestate/3124/1920x1920wm-x.jpg",
     "https://www.premiareality.cz/files/importestate/3124/1920x1920wm-x.jpg"),
])
def test_original_misto_nahledu(thumb, original):
    assert PremiaRealityScraper._original_url(thumb) == original
