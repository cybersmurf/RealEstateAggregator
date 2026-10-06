"""Nemovitosti Znojmo – typ podle popisku webu, štítek rezervace, obec z řádku „Adresa".

6. 10. 2026: typ se určoval z názvu podle pořadí klíčových slov („byt" první), takže
„Dům v centru Dyjákovic … nebo 3 byty" a hotely s „ubytováním" končily mezi byty.
Rezervovaných nabídek bylo 8 ze 100 a u nás vypadaly jako volné.
"""
from pathlib import Path

import pytest

from core.filters import FilterManager
from core.scrapers.nemovitostiznojmo_scraper import NemovitostiZnojmoScraper

FIX = Path(__file__).parent / "fixtures" / "nemovitostiznojmo"
BASE = "https://www.nemovitostiznojmo.cz"


def _detail(fixture: str, external_id: str, **list_fields) -> dict:
    html = (FIX / fixture).read_text(encoding="utf-8")
    item = {"url": f"{BASE}/x/detail/{external_id}", "title": "", **list_fields}
    return NemovitostiZnojmoScraper()._parse_detail_page(html, item)


class TestNemZnojmoVypis:
    def setup_method(self):
        html = (FIX / "list_page1.html").read_text(encoding="utf-8")
        self.items, self.has_next = NemovitostiZnojmoScraper()._parse_list_page(html)
        self.by_id = {item["url"].rsplit("/", 1)[-1]: item for item in self.items}

    def test_vypis_ma_dvanact_nabidek_a_dalsi_stranku(self):
        assert len(self.items) == 12
        assert self.has_next is True

    def test_karta_nese_popisek_typu_z_webu(self):
        assert self.by_id["10195503"]["site_type"] == "Rodinný dům"
        assert self.by_id["10198401"]["site_type"] == "4+kk"
        assert self.by_id["10195516"]["site_type"] == "Obchodní prostor"
        assert self.by_id["10186789"]["site_type"] == "Chata"
        assert self.by_id["10193921"]["site_type"] == "Garáž"

    def test_rezervovane_karty_se_poznaji(self):
        reserved = {ext_id for ext_id, item in self.by_id.items() if item["reserved"]}
        assert reserved == {"10198401", "10195503", "10186784"}

    def test_nazev_a_cena_z_karty(self):
        item = self.by_id["10195503"]
        assert item["title"] == "Prodej rodinného domu, 124m2, k.ú. Chvalovice u Znojma"
        assert item["price_text"] == "3 490 000Kč"


class TestNemZnojmoTypPodlePopisku:
    @pytest.mark.parametrize("label,expected", [
        ("Rodinný dům", "Dům"),
        ("Vícegenerační dům", "Dům"),
        ("Vila", "Dům"),
        ("Činžovní dům", "Dům"),
        ("Domy Rodinný dům", "Dům"),
        ("Rekreační objekty Chata", "Chata"),
        ("Chata", "Chata"),
        ("4+kk", "Byt"),
        ("3+1", "Byt"),
        ("atypický/jiný", "Byt"),
        ("Byty 4+kk", "Byt"),
        ("Pozemek", "Pozemek"),
        ("Pozemky Pozemek, komerční", "Pozemek"),
        ("Zahrada", "Pozemek"),
        ("Garáž", "Garáž"),
        ("Ostatní Vinný sklep", "Ostatní"),
        ("Obchodní prostor", "Komerční"),
        ("Kancelář", "Komerční"),
        ("Komerční nemovitosti Hotel", "Komerční"),
        ("Pension", "Komerční"),
        ("Skladovací prostory", "Komerční"),
        ("Výrobní prostory", "Komerční"),
        ("Administrativní budova", "Komerční"),
    ])
    def test_popisek_webu_urci_typ(self, label, expected):
        assert NemovitostiZnojmoScraper._type_from_site_label(label) == expected

    def test_prazdny_nebo_neznamy_popisek_nic_neurci(self):
        assert NemovitostiZnojmoScraper._type_from_site_label("") is None
        assert NemovitostiZnojmoScraper._type_from_site_label("Samostatně stojící") is None


class TestNemZnojmoDetail:
    def test_dum_se_tremi_byty_v_nazvu_je_dum(self):
        listing = _detail("detail_dum_dyjakovice_10032294.html", "10032294")
        assert "3 byty" in listing["title"]
        assert listing["property_type"] == "Dům"
        assert listing["offer_type"] == "Prodej"
        assert listing["price"] == 3_800_000
        assert listing["municipality"] == "Dyjákovice"
        assert "price_note" not in listing and "sold" not in listing

    def test_hotel_s_ubytovanim_neni_byt(self):
        listing = _detail("detail_hotel_10045838.html", "10045838")
        assert "ubytování" in listing["title"]
        assert listing["property_type"] == "Komerční"

    def test_pronajem_bytu_podle_radku_typ_prodeje(self):
        listing = _detail("detail_byt_pronajem_10187903.html", "10187903")
        assert listing["property_type"] == "Byt"
        assert listing["offer_type"] == "Pronájem"
        assert listing["price"] == 15_000

    def test_rezervovana_nabidka_ma_stitek_a_cenu(self):
        listing = _detail("detail_dum_rezervace_chvalovice_10195503.html", "10195503")
        assert listing["price_note"] == "Rezervace"
        assert listing["keep_last_price"] is True
        assert listing["price"] == 3_490_000
        assert listing["municipality"] == "Chvalovice"
        assert "sold" not in listing

    def test_rezervace_jen_ze_stitku_karty(self):
        # Detail o rezervaci mlčí, štítek byl jen na kartě výpisu
        listing = _detail("detail_dum_dyjakovice_10032294.html", "10032294", reserved=True)
        assert listing["price_note"] == "Rezervace"

    def test_cast_mesta_ma_obec_znojmo(self):
        # „Přesná adresa" je „Parcela 3070, Načeratice", řádek „Adresa" říká „Znojmo, Načeratice"
        listing = _detail("detail_dum_naceratice_10047380.html", "10047380")
        assert listing["municipality"] == "Znojmo"
        assert listing["property_type"] == "Dům"
        assert listing["latitude"] == pytest.approx(48.8175, abs=0.001)

    def test_novostavba_v_naceraticich_projde_geografickym_filtrem(self):
        listing = _detail("detail_dum_naceratice_10047380.html", "10047380")
        assert FilterManager().passes_search_filters(listing) is True

    def test_prodana_nabidka_se_oznaci_k_deaktivaci(self):
        html = """<html><body><h1>Prodej rodinného domu, Znojmo</h1>
        <table class="table--info detail-main--table">
          <tr><td>Typ nemovitosti</td><th>Domy</th></tr><tr><td>Upřesnění</td><th>Rodinný dům</th></tr>
          <tr><td>Stav inzerátu</td><th>Prodáno</th></tr>
        </table></body></html>"""
        listing = NemovitostiZnojmoScraper()._parse_detail_page(html, {"url": f"{BASE}/x/detail/1", "title": ""})
        assert listing["sold"] is True
        assert "price_note" not in listing

    def test_bez_popisku_rozhodne_popisek_z_vypisu_a_pak_nazev(self):
        html = "<html><body><h1>Prodej vily 5+1 s garáží a zahradou</h1></body></html>"
        scraper = NemovitostiZnojmoScraper()
        from_list = scraper._parse_detail_page(html, {"url": f"{BASE}/x/detail/2", "title": "", "site_type": "Vila"})
        from_title = scraper._parse_detail_page(html, {"url": f"{BASE}/x/detail/2", "title": ""})
        assert from_list["property_type"] == "Dům"
        assert from_title["property_type"] == "Dům"
