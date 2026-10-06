"""HV Reality – stav nabídky z REST (acf.advert_state) a typ nemovitosti ze slugu URL.

6. 10. 2026: rezervaci web na detailu nijak neoznačí (byt v Hevlíně byl v administraci
„REZERVOVÁNO", stránka mlčela), takže rezervované nabídky vypadaly jako volné.
„Prodej chalupy 158 m2, pozemek 849 m2" se podle názvu ukládal jako pozemek
a „Prodej ubytovacích prostor" jako byt.
"""
import json
from pathlib import Path

import pytest

from core.scrapers.hvreality_scraper import HvRealityScraper

FIX = Path(__file__).parent / "fixtures" / "hvreality"
SALE = "https://hvreality.cz/prodej-nemovitosti/"
RENT = "https://hvreality.cz/pronajem-nemovitosti/"
HEVLIN_URL = SALE + "prodej-bytu-2-1-hevlin-okres-znojmo-prodej-bytu-2-1-48-m2-hevlin-1087/"
CHALUPA_URL = SALE + "prodej-chalupy-hrabetice-okres-znojmo-prodej-chalupy-s-vinnym-sklepem-a-zahradou-hrabetice-1232-2/"


def _rest_items() -> dict:
    data = json.loads((FIX / "rest_prodej_stav.json").read_text(encoding="utf-8"))
    return {item["url"]: item for item in HvRealityScraper.parse_rest_items(data)}


def _find(items: dict, fragment: str) -> dict:
    return next(item for url, item in items.items() if fragment in url)


class TestHvRealityRestStav:
    def test_polozky_nesou_stav_cenu_a_okres(self):
        items = _rest_items()
        hevlin = items[HEVLIN_URL]
        assert hevlin["state"] == "reserved"
        assert hevlin["price"] == 2_990_000
        assert hevlin["county"] == "Znojmo"
        assert hevlin["district_slug"] == "znojmo"

    def test_volna_a_prodana_nabidka(self):
        items = _rest_items()
        assert _find(items, "rodinneho-domu-kyjovice")["state"] == "active"
        sold = [item for item in items.values() if item["state"] == "sold"]
        assert len(sold) == 4
        assert _find(items, "rodinneho-domu-plavec")["state"] == "sold"

    def test_nulova_cena_z_administrace_neni_cena(self):
        items = _rest_items()
        assert _find(items, "prodej-restaurace")["price"] is None

    def test_stary_inzerat_bez_okresu_ve_slugu_ma_okres_z_rest(self):
        items = _rest_items()
        old = next(item for item in items.values() if not item["district_slug"])
        assert old["county"] == "Znojmo"
        assert old["state"] == "sold"

    def test_pronajmy_stav_v_rest_nemaji(self):
        data = json.loads((FIX / "rest_pronajem.json").read_text(encoding="utf-8"))
        items = HvRealityScraper.parse_rest_items(data)
        assert len(items) == 3
        assert all(item["state"] is None and item["price"] is None and item["county"] is None for item in items)
        assert all(item["district_slug"] == "znojmo" for item in items)

    @pytest.mark.parametrize("acf,expected", [
        ({"advert_state": "V NABÍDCE", "advert_sold": False}, "active"),
        ({"advert_state": "REZERVOVÁNO", "advert_sold": False}, "reserved"),
        ({"advert_state": "PRODÁNO", "advert_sold": True}, "sold"),
        ({"advert_state": "PRONAJATO"}, "sold"),
        ({"advert_state": "V NABÍDCE", "advert_sold": True}, "sold"),
        ({}, None),
        ([], None),
        (None, None),
    ])
    def test_stav_z_pole_acf(self, acf, expected):
        assert HvRealityScraper._rest_state(acf) == expected

    def test_json_s_php_varovanim_pred_polem(self):
        text = '<br />\n<b>Warning</b>:  Undefined array key "type" in <b>Post.php</b> on line <b>188</b><br />\n[{"id": 1, "link": "x"}]'
        assert HvRealityScraper._loads_rest(text) == [{"id": 1, "link": "x"}]
        assert HvRealityScraper._loads_rest('[{"id": 2}]') == [{"id": 2}]


class TestHvRealityTypZeSlugu:
    @pytest.mark.parametrize("url,expected", [
        (CHALUPA_URL, "Dům"),
        (SALE + "prodej-chaty-tavikovice-okres-znojmo-prodej-chaty-68-m2-tavikovice-1111/", "Dům"),
        (SALE + "prodej-rodinneho-domu-kyjovice-okres-znojmo-prodej-rodinneho-domu-3-1-150-m2-kyjovice-1230/", "Dům"),
        (SALE + "prodej-cinzovniho-domu-znojmo-okres-znojmo-prodej-bytoveho-domu-ve-znojme-1116/", "Dům"),
        (SALE + "prodej-rd-205-m2-pozemek-1135-m2-melcany/", "Dům"),
        (SALE + "prodej-nemovitosti-pro-ubytovani-lukov-okres-znojmo-prodej-ubytovacich-prostor-564-m2-lukov-1/", "Komerční"),
        (SALE + "prodej-restaurace-lesna-okres-znojmo-prodej-restauracnich-prostor-450-m2-lesna-2/", "Komerční"),
        (RENT + "pronajem-kancelare-brno-okres-brno-mesto-pronajem-kancelarskych-prostor-24-m2-brno-3/", "Komerční"),
        (SALE + "prodej-bytu-2-1-hevlin-okres-znojmo-prodej-bytu-2-1-48-m2-hevlin-1087/", "Byt"),
        (RENT + "pronajem-bytu-1-kk-znojmo-okres-znojmo-pronajem-bytu-1-kk-36-m2-znojmo-4/", "Byt"),
        (SALE + "prodej-stavebniho-pozemku-sanov-okres-znojmo-prodej-stavebni-parcely-1779-m2-sanov-5/", "Pozemek"),
        (SALE + "prodej-zahrady-tavikovice-okres-znojmo-prodej-zahrady-757-m2-tavikovice-6/", "Pozemek"),
        (SALE + "prodej-sadu-vinice-kridluvky-okres-znojmo-prodej-sadu-vinice-22053-m2-kridluvky-7/", "Pozemek"),
        (SALE + "prodej-garazoveho-stani-brno-okres-brno-mesto-prodej-garazoveho-stani-14-m2-brno-8/", "Garáž"),
        (SALE + "prodej-vinneho-sklepa-hrabetice-okres-znojmo-prodej-vinneho-sklepa-50-m2-hrabetice-9/", "Ostatní"),
    ])
    def test_typ_podle_zacatku_slugu(self, url, expected):
        assert HvRealityScraper._type_from_url(url) == expected

    def test_slug_bez_znameho_typu_nic_neurci(self):
        assert HvRealityScraper._type_from_url(SALE + "prodej-apartmanu-znojmo-okres-znojmo-prodej-apartmanu-210-m2-10/") is None
        assert HvRealityScraper._type_from_url(RENT + "pronajem-nemovitosti-7-m2-znojmo/") is None

    def test_chalupa_s_pozemkem_v_nazvu_je_dum(self):
        html = (FIX / "detail_chalupa_hrabetice.html").read_text(encoding="utf-8")
        listing = HvRealityScraper()._parse_detail_page(html, {"url": CHALUPA_URL, "title": ""})
        assert "pozemek" in listing["title"]
        assert listing["property_type"] == "Dům"

    def test_stary_slug_bez_typu_pouzije_nazev(self):
        html = "<html><body><h1>Pronájem nemovitosti 7 m2 – Znojmo</h1></body></html>"
        listing = HvRealityScraper()._parse_detail_page(html, {"url": RENT + "pronajem-nemovitosti-7-m2-znojmo/", "title": ""})
        assert listing["property_type"] == "Ostatní"
        assert listing["offer_type"] == "Pronájem"


class TestHvRealityRezervace:
    def _hevlin(self, **rest_fields) -> dict:
        html = (FIX / "detail_rezervace_hevlin.html").read_text(encoding="utf-8")
        return HvRealityScraper()._parse_detail_page(html, {"url": HEVLIN_URL, "title": "", **rest_fields})

    def test_rezervace_z_rest_zustava_se_stitkem(self):
        listing = self._hevlin(state="reserved", price=2_990_000.0, county="Znojmo")
        assert listing["is_sold"] is False
        assert listing["price_note"] == "Rezervace"
        assert listing["keep_last_price"] is True
        assert listing["price"] == 2_990_000
        assert listing["municipality"] == "Hevlín"
        assert listing["district"] == "Znojmo"

    def test_detail_sam_o_rezervaci_nevi(self):
        # Proto se stav bere z REST – stránka rezervovaného bytu vypadá jako volná
        listing = self._hevlin()
        assert listing["is_sold"] is False
        assert "price_note" not in listing

    def test_prodano_z_rest_se_deaktivuje(self):
        assert self._hevlin(state="sold")["is_sold"] is True

    def test_cena_z_rest_ma_prednost_pred_strankou(self):
        assert self._hevlin(state="active", price=2_890_000.0)["price"] == 2_890_000
        assert self._hevlin(state="active", price=None)["price"] == 2_990_000

    def test_stav_z_detailu_kdyz_rest_mlci(self):
        from bs4 import BeautifulSoup

        def _state(description: str):
            return HvRealityScraper._page_state(
                BeautifulSoup(f'<meta name="description" content="{description}">', "html.parser"))

        assert _state("PRONAJATO: Pronájem bytu 2+1 Znojmo - Jihomoravský kraj.") == "sold"
        assert _state("PRODÁNO: Prodej rodinného domu Znojmo - okres Znojmo.") == "sold"
        assert _state("REZERVOVÁNO: Pronájem bytu 1+kk Znojmo - Jihomoravský kraj.") == "reserved"
        assert _state("Pronájem bytu 1+kk Znojmo - Jihomoravský kraj.") is None

    def test_okres_z_rest_kdyz_ho_stranka_ani_slug_nema(self):
        html = "<html><body><h1>Prodej rodinného domu 120 m2 – Jaroslavice</h1></body></html>"
        item = {"url": SALE + "prodej-rodinneho-domu-120-m2-pozemek-1-250-m2-jaroslavice/", "title": "", "county": "Znojmo"}
        listing = HvRealityScraper()._parse_detail_page(html, item)
        assert listing["district"] == "Znojmo"
        assert listing["property_type"] == "Dům"
