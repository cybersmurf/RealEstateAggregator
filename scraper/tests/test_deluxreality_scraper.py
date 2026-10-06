"""DeluXreality – lokalita, typ a cena z karty výpisu a postranního panelu detailu.

6. 10. 2026: lokalita se brala jako první text se slovem „Znojmo" na stránce, což je titulek
„… – DELUX Znojmo" – okres Znojmo tak dostaly i investiční apartmány v Polsku (s cenou
50 000 Kč místo 1 850 000 Kč). „Novostavba RD 4+kk Šatov" byla typu Ostatní a pronájem
„Byt 2+kk ulice Šatovská 9" se ukládal jako prodej za 11 500 Kč.
"""
from pathlib import Path

import pytest

from core.scrapers.deluxreality_scraper import DeluxRealityScraper

FIX = Path(__file__).parent / "fixtures" / "deluxreality"
BASE = "https://deluxreality.cz/nemovitosti/"


def _cards(fixture: str, offer_type: str) -> dict:
    html = (FIX / fixture).read_text(encoding="utf-8")
    return {item["url"]: item for item in DeluxRealityScraper.parse_list_page(html, offer_type)}


def _detail(fixture: str, list_item: dict) -> dict:
    html = (FIX / fixture).read_text(encoding="utf-8")
    return DeluxRealityScraper().parse_detail_page(html, dict(list_item))


class TestDeluxVypis:
    def test_karty_prodeje(self):
        cards = _cards("list_prodej_2.html", "Sale")
        assert len(cards) == 8
        card = cards[BASE + "prodej-rodinneho-domu-41-vetrna-946-46-miroslav/"]
        assert card["offer_type"] == "Sale"
        assert card["title"] == "Prodej domu 4+1 Větrná, Miroslav"
        assert card["address"] == "Větrná 946/46, Miroslav"
        assert card["price_text"] == "6 500 000 Kč"
        assert card["meta"] == ["Rodinný dům", "4+1", "137 m²"]
        assert card["badge"] == "REZERVOVÁNO"

    def test_karty_pronajmu_maji_typ_nabidky_z_filtru(self):
        cards = _cards("list_pronajem.html", "Rent")
        assert len(cards) == 9
        assert all(card["offer_type"] == "Rent" for card in cards.values())
        # Název o pronájmu nic neříká – rozhoduje filtr výpisu
        assert cards[BASE + "byt-2kk-ulice-satovska-9/"]["title"] == "Byt 2+kk ulice Šatovská 9"

    def test_zaloha_bez_karet_vrati_aspon_odkazy(self):
        html = f'<a href="{BASE}?typ=prodej">filtr</a><a href="{BASE}rodinny-dum-vrbovec/">detail</a><a href="{BASE}">vše</a>'
        items = DeluxRealityScraper.parse_list_page(html, "Sale")
        assert items == [{"url": BASE + "rodinny-dum-vrbovec/", "offer_type": "Sale"}]


class TestDeluxDetail:
    def test_rezervovany_dum_v_miroslavi(self):
        card = _cards("list_prodej_2.html", "Sale")[BASE + "prodej-rodinneho-domu-41-vetrna-946-46-miroslav/"]
        listing = _detail("detail_dum_rezervace_miroslav.html", card)
        assert listing["property_type"] == "House"
        assert listing["offer_type"] == "Sale"
        assert listing["price"] == 6_500_000
        assert listing["location_text"] == "Větrná 946/46, Miroslav"
        assert listing["municipality"] == "Miroslav"
        assert listing["district"] == "Znojmo"  # popis: „… v Miroslavi, okres Znojmo"
        assert listing["price_note"] == "Rezervace"
        assert listing["keep_last_price"] is True
        assert listing["area_built_up"] == 137

    def test_apartmany_v_polsku_nemaji_okres_znojmo(self):
        card = _cards("list_prodej_2.html", "Sale")[BASE + "investicni-apartmany-polsko/"]
        listing = _detail("detail_apartmany_polsko.html", card)
        assert "district" not in listing
        assert "municipality" not in listing
        assert listing["location_text"] == "Świeradów-Zdrój Polsko"
        assert "Znojmo" not in listing["location_text"]
        assert listing["price"] == 1_850_000
        assert listing["property_type"] == "Apartment"

    def test_rd_v_nazvu_je_dum(self):
        card = _cards("list_prodej_2.html", "Sale")[BASE + "novostavba-rd-4kk-satov/"]
        assert card["meta"][0] == "4+kk"  # karta typ neuvádí, jen dispozici
        listing = _detail("detail_rd_satov.html", card)
        assert listing["property_type"] == "House"
        assert listing["price"] == 6_999_000
        assert listing["municipality"] == "Šatov"

    def test_pronajem_bez_slova_pronajem_v_nazvu(self):
        card = _cards("list_pronajem.html", "Rent")[BASE + "byt-2kk-ulice-satovska-9/"]
        listing = _detail("detail_pronajem_satovska_9.html", card)
        assert listing["offer_type"] == "Rent"
        assert listing["price"] == 11_500
        assert listing["property_type"] == "Apartment"
        # Adresa je jen ulice; že jde o Znojmo, říká popis („… ve Znojmě")
        assert listing["location_text"] == "Šatovská 9"
        assert "municipality" not in listing
        assert listing["district"] == "Znojmo"

    def test_najem_pod_deset_tisic_ma_cenu(self):
        card = _cards("list_pronajem.html", "Rent")[BASE + "pronajem-bytu-1kk-ulice-velka-michalska-znojmo/"]
        listing = _detail("detail_pronajem_9000.html", card)
        assert listing["price"] == 9_000
        assert listing["offer_type"] == "Rent"
        assert listing["municipality"] == "Znojmo"
        assert listing["district"] == "Znojmo"

    def test_okres_uvedeny_v_adrese(self):
        card = _cards("list_prodej_1.html", "Sale")[BASE + "prodej-rodinneho-domu-4kk-sumna/"]
        listing = _detail("detail_dum_sumna.html", card)
        assert listing["location_text"] == "Šumná, okres Znojmo"
        assert listing["municipality"] == "Šumná"
        assert listing["district"] == "Znojmo"

    def test_obec_s_cislem_popisnym_neni_ulice_ani_obec(self):
        card = _cards("list_prodej_1.html", "Sale")[BASE + "prodej-rodinneho-domu-31-vranovska-ves/"]
        listing = _detail("detail_dum_vranovska_ves.html", card)
        assert listing["location_text"] == "Vranovská Ves 114"
        assert "municipality" not in listing
        assert listing["property_type"] == "House"
        assert listing["price"] == 1_890_000

    def test_detail_bez_karty_si_vystaci_s_panelem(self):
        listing = _detail("detail_dum_rezervace_miroslav.html",
                          {"url": BASE + "prodej-rodinneho-domu-41-vetrna-946-46-miroslav/", "offer_type": "Sale"})
        assert listing["price"] == 6_500_000
        assert listing["price_note"] == "Rezervace"
        assert listing["property_type"] == "House"

    def test_prodana_nabidka_se_oznaci_k_deaktivaci(self):
        html = """<html><body><h1>Prodej bytu 2+1, Znojmo</h1>
        <aside class="dx-ps-sidebar"><div class="dx-attr-price">3 200 000 Kč</div><div class="dx-attr-status">prodáno</div></aside>
        </body></html>"""
        listing = DeluxRealityScraper().parse_detail_page(html, {"url": BASE + "prodej-bytu-21-znojmo/", "offer_type": "Sale"})
        assert listing["sold"] is True
        assert "price_note" not in listing

    def test_cena_na_dotaz_jde_do_poznamky(self):
        html = """<html><body><h1>Prodej vily, Znojmo</h1>
        <aside class="dx-ps-sidebar"><div class="dx-attr-price">Cena na dotaz</div><div class="dx-attr-status">aktivní</div></aside>
        </body></html>"""
        listing = DeluxRealityScraper().parse_detail_page(html, {"url": BASE + "prodej-vily-znojmo/", "offer_type": "Sale"})
        assert listing["price"] is None
        assert listing["price_note"] == "Cena na dotaz"


class TestDeluxPomocneFunkce:
    @pytest.mark.parametrize("title,label,expected", [
        ("Novostavba RD 4+kk Šatov", "4+kk", "House"),
        ("RD, ul. Stanislavova", "—", "House"),
        ("Rodinný Dům Vrbovec", "dum", "House"),
        ("Prodej domu 4+1 Větrná, Miroslav", "Rodinný dům", "House"),
        ("Prodej bytu 4+kk s garáží, Přímětická, Znojmo", "Byt", "Apartment"),
        ("Byt 2+kk ulice Šatovská 9", "Byt", "Apartment"),
        ("Investiční apartmány Polsko", "1+kk", "Apartment"),
        ("Stavební pozemek Mikulovice", "Pozemek", "Land"),
        ("Stavební pozemek pro rodinný dům, Únanov", "", "Land"),
        ("Zahrada, Znojmo-Klínek", "28 x 36 m", "Land"),
        ("Obchodní prostory", "—", "Commercial"),
        ("Prodej chaty se zahradou, Bítov", "", "Cottage"),
        ("Nabídka týdne", "", "Other"),
    ])
    def test_typ_z_popisku_karty_a_nazvu(self, title, label, expected):
        assert DeluxRealityScraper._detect_property_type(title, label) == expected

    @pytest.mark.parametrize("text,expected", [
        ("6 500 000 Kč", 6_500_000),
        ("9 000 Kč /měs.", 9_000),
        ("11.500 Kč", 11_500),
        ("1\xa0850\xa0000 Kč", 1_850_000),
        ("Cena na dotaz", None),
        ("", None),
    ])
    def test_cena_z_textu(self, text, expected):
        assert DeluxRealityScraper._parse_price_text(text) == expected

    @pytest.mark.parametrize("location,title,description,expected", [
        ("Přímětická, Znojmo", "Prodej bytu", "", ("Znojmo", "Znojmo")),
        ("Šatovská 495/9, Znojmo - Oblekovice", "Prodej bytu", "", ("Znojmo", "Znojmo")),
        ("Šmilovského, Brno-Slatina", "Prodej rodinného domu", "", ("Brno-Slatina", "Brno-město")),
        ("Šumná, okres Znojmo", "Prodej rodinného domu", "", ("Šumná", "Znojmo")),
        ("Vrbovec", "Rodinný Dům Vrbovec", "Dům stojí v klidné části obce.", ("Vrbovec", None)),
        ("Vrbovec", "Rodinný Dům Vrbovec", "Do práce ve Znojmě je to deset minut.", ("Vrbovec", None)),
        ("Hevlín", "Prodej pozemku Hevlín", "Pozemek v obci Hevlín, okres Znojmo.", ("Hevlín", "Znojmo")),
        ("Šatovská 9", "Byt 2+kk ulice Šatovská 9", "Byt na ulici Šatovská ve Znojmě.", (None, "Znojmo")),
        ("17.listopadu 18", "Obchodní prostory", "", (None, None)),
        ("Świeradów-Zdrój Polsko", "Investiční apartmány Polsko", "Naše kancelář sídlí ve Znojmě.", (None, None)),
    ])
    def test_obec_a_okres_z_adresy(self, location, title, description, expected):
        assert DeluxRealityScraper._parse_location(location, title, description) == expected
