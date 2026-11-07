"""
Unit testy parseru RealityCechy.cz nad uloženými fixturami (tests/fixtures/realitycechy/).
Bez živého HTTP a bez DB.
"""
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers import realitycechy_scraper
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
        assert len(DEFAULT_LISTS) == 12
        assert {d for _, _, _, d, _ in DEFAULT_LISTS} == {"Znojmo", "Brno-venkov", "Brno-město", "Břeclav"}
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


class TestRealityCechyDetailDumBezPlochy:
    """Hrušovany n. J.: inzerát uvádí jen výměru pozemku, plochu domu ne."""

    URL = "https://www.realitycechy.cz/nemovitost/prodej/hrusovany-nad-jevisovkou-rd-4-1-zahrada-garaz-rodinny-dum/20304653"

    def setup_method(self):
        self.scraper = RealityCechyScraper()
        self.d = self.scraper.parse_detail_page(
            _load("detail_dum_bez_plochy.html"), _item(self.URL, "20304653"), "Dům", "Prodej", "Znojmo")

    def test_vymera_pozemku_neni_plocha_domu(self):
        assert self.d["area_land"] == 986
        assert self.d["area_built_up"] is None

    def test_kontakt_na_maklere(self):
        assert self.d["seller_name"] == "Radek Novák, Ing."
        assert self.d["seller_email"] == "r.novak@rkcoloseum.cz"
        assert self.d["seller_company"] == "COLOSEUM NEMOVITOSTI s.r.o."
        assert "seller_phone" not in self.d

    def test_obytna_plocha_ma_prednost_pred_zastavenou(self):
        html = ("<html><body><h1 class='entry-title'>RD s dvorem</h1><table class='detail_table_pravy'>"
                "<tr><th>Druh:</th><td>rodinný dům</td></tr>"
                "<tr><th>Zastavěná plocha:</th><td>373 m<sup>2</sup></td></tr>"
                "<tr><th>Obytná plocha:</th><td>200 m<sup>2</sup></td></tr>"
                "<tr><th>Výměra pozemku:</th><td>373 m<sup>2</sup></td></tr></table></body></html>")
        d = self.scraper.parse_detail_page(html, _item(self.URL, "1"), "Dům", "Prodej", "Znojmo")
        assert d["area_built_up"] == 200
        assert d["area_land"] == 373

    def test_zastupny_obrazek_z_vypisu_neni_fotka(self):
        item = _item(self.URL, "20304653", thumb="https://www.realitycechy.cz/www/images/default_foto.jpg")
        html = "<html><body><h1 class='entry-title'>Byt 2+kk</h1></body></html>"
        assert self.scraper.parse_detail_page(html, item, "Byt", "Prodej", "Znojmo")["photos"] == []


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


# ── redesign výpisu a detailu (11/2026), dvoufázový běh ─────────────────────


class TestRealityCechyVypisRcCard:
    """Výpis po redesignu: 20 karet article.rc-card, počet v .rc-toolbar__count (Brno-venkov, domy)."""

    def setup_method(self):
        self.scraper = RealityCechyScraper()
        self.items, self.total, self.has_next = self.scraper.parse_list_page(
            _load("list_domy_brno_venkov_rc_card.html"), page=1)

    def test_pocet_karet_celkovy_pocet_a_dalsi_strana(self):
        assert len(self.items) == 20
        assert self.total == 200
        assert self.has_next is True

    def test_prvni_karta(self):
        first = self.items[0]
        assert first["external_id"] == "21218913"
        assert first["url"] == ("https://www.realitycechy.cz/nemovitost/prodej/"
                                "celorocne-obyvatelna-chata-s-vinohradem-a-nadhernym-vyhledem-v-dolnich-kounicich/21218913")
        assert first["title"].startswith("Celoročně obyvatelná chata s vinohradem")
        assert first["municipality"] == "Dolní Kounice"
        assert first["price"] == 2_250_000

    def test_external_id_jsou_unikatni_a_ciselna(self):
        ids = [i["external_id"] for i in self.items]
        assert len(set(ids)) == 20
        assert all(i.isdigit() for i in ids)

    def test_nahled_je_vlastni_fotka_karty(self):
        assert all(f"/{i['external_id']}/" in i["thumb"] for i in self.items)

    def test_ceny_z_vypisu(self):
        prices = {i["external_id"]: i["price"] for i in self.items}
        assert prices["21218475"] == 5_697_000
        assert prices["21215615"] == 15_911_735

    def test_cena_v_rk_a_cena_za_m2(self):
        html = _list_html([_card("1", price="cena v RK"), _card("2", price="135 000 Kč/m2")], total=2)
        items, _, _ = self.scraper.parse_list_page(html)
        assert items[0]["price"] is None
        assert items[1]["price"] == 135_000

    def test_prectrnuta_puvodni_cena_se_nebere(self):
        html = _list_html([_card("1", price="<del>6 000 000 Kč</del> 5 500 000 Kč")], total=1)
        items, _, _ = self.scraper.parse_list_page(html)
        assert items[0]["price"] == 5_500_000

    def test_posledni_strana_nema_dalsi(self):
        _, total, has_next = self.scraper.parse_list_page(_list_html([_card("1")], total=181, has_next=False), page=10)
        assert total == 181
        assert has_next is False

    def test_zaloha_ld_json_kdyz_chybi_karty(self):
        html = _load("list_domy_brno_venkov_rc_card.html").replace('class="rc-card ', 'class="x-card ')
        items, total, _ = self.scraper.parse_list_page(html, page=1)
        assert len(items) == 20
        assert items[0]["external_id"] == "21218913"
        assert items[0]["price"] is None
        assert total == 200


class TestRealityCechyStrankovani:
    @pytest.mark.parametrize("total,per_page,expected", [
        (200, 20, 10),
        (503, 20, 26),   # byty Brno-město – víc než dřívější strop 20 stránek
        (181, 20, 10),
        (20, 20, 1),
        (7, 20, 1),
        (0, 20, 0),
        (200, 0, 0),
    ])
    def test_pages_needed(self, total, per_page, expected):
        assert RealityCechyScraper.pages_needed(total, per_page) == expected

    def test_plny_beh_nema_strop_20_stranek(self):
        assert RealityCechyScraper.MAX_LIST_PAGES >= 26

    def test_casovy_rozpocet_pod_limitem_ulohy(self):
        assert RealityCechyScraper.TIME_BUDGET_SECONDS < 45 * 60
        assert RealityCechyScraper.out_of_time(0.0, 36 * 60) is True
        assert RealityCechyScraper.out_of_time(0.0, 35 * 60) is False


class TestRealityCechyVyberDetailu:
    def test_novy_a_zmenena_cena_ano_stejna_ne(self):
        items = [
            {"external_id": "1", "price": 5_000_000.0},   # známý, stejná cena
            {"external_id": "2", "price": 4_500_000.0},   # známý, zlevněný
            {"external_id": "3", "price": 3_000_000.0},   # nový
            {"external_id": "4", "price": None},          # známý, ve výpisu „cena v RK"
            {"external_id": "5", "price": 2_000_000.0},   # známý bez uložené ceny
        ]
        known = {"1": 5_000_000.0, "2": 4_900_000.0, "4": 1_000_000.0, "5": None}
        selected = [i["external_id"] for i in RealityCechyScraper.select_for_detail(items, known)]
        assert selected == ["2", "5", "3"]   # napřed změny ceny, potom nové


class TestRealityCechyPredfiltr:
    @staticmethod
    def _item(**kw: Any) -> Dict[str, Any]:
        base = {"external_id": "1", "title": "Prodej rodinného domu", "municipality": "Ivančice",
                "price": 5_000_000.0, "property_type": "Dům", "offer_type": "Prodej", "district": "Brno-venkov"}
        base.update(kw)
        return base

    def test_dum_v_okrese_projde(self):
        assert RealityCechyScraper.passes_filters(self._item()) is True

    def test_dum_nad_cenovy_limit_se_nestahuje(self):
        assert RealityCechyScraper.passes_filters(self._item(price=13_290_000.0)) is False

    def test_chata_ve_vypisu_domu_cenovy_limit_nema(self):
        item = self._item(title="Prodej celoroční chaty 54 m2", price=12_000_000.0)
        assert RealityCechyScraper.passes_filters(item) is True

    def test_dum_bez_ceny_projde(self):
        assert RealityCechyScraper.passes_filters(self._item(price=None)) is True

    def test_pozemek_nad_limit_se_nestahuje(self):
        item = self._item(property_type="Pozemek", district="Znojmo", municipality="Znojmo", price=4_000_000.0)
        assert RealityCechyScraper.passes_filters(item) is False

    @pytest.mark.parametrize("municipality,expected", [
        ("Mikulov", True),      # Pálava – částečný okres
        ("Valtice", False),     # okres Břeclav mimo vybrané obce
    ])
    def test_breclav_jen_vybrane_obce(self, municipality, expected):
        item = self._item(district="Břeclav", municipality=municipality)
        assert RealityCechyScraper.passes_filters(item) is expected


class TestRealityCechySmazanyInzerat:
    """Smazaný inzerát vrací 200, „Nemovitost již není v nabídce" a pod tím karty cizích nabídek."""

    URL = "https://www.realitycechy.cz/nemovitost/prodej/prodej-rodinneho-domu-181-m2-s-bazenem-veverska-bityska/21188870"

    def setup_method(self):
        self.scraper = RealityCechyScraper()
        self.html = _load("detail_smazany.html")

    def test_smazany_se_pozna(self):
        assert RealityCechyScraper.is_deleted_page(self.html) is True

    def test_smazany_se_neparsuje_jako_inzerat(self):
        item = _item(self.URL, "21188870", thumb="https://www.realitycechy.cz/foto/mq/21189000/21188870/21188870_01.webp")
        assert self.scraper.parse_detail_page(self.html, item, "Dům", "Prodej", "Brno-venkov") is None

    def test_pozna_se_i_jen_podle_textu(self):
        html = "<html><body><h1 class='entry-title'>Dům</h1><span>Nemovitost již\xa0není v nabídce. Vyberte si…</span></body></html>"
        assert RealityCechyScraper.is_deleted_page(html) is True

    @pytest.mark.parametrize("fixture", [
        "detail_dum_ivancice_rc.html", "detail_dum.html", "detail_byt.html", "detail_pozemek.html",
        "detail_chata.html", "detail_dum_bez_plochy.html",
    ])
    def test_zivy_inzerat_neni_smazany(self, fixture):
        assert RealityCechyScraper.is_deleted_page(_load(fixture)) is False


class TestRealityCechyDetailRedesign:
    """Detail po redesignu: galerie desktop + mobil, pod popisem karty podobných nabídek (.rc-card)."""

    URL = "https://www.realitycechy.cz/nemovitost/prodej/prodej-rodinneho-domu-97m2-ivancice-budkovice/21218475"

    def setup_method(self):
        self.scraper = RealityCechyScraper()
        self.html = _load("detail_dum_ivancice_rc.html")
        self.d = self.scraper.parse_detail_page(self.html, _item(self.URL, "21218475"), "Dům", "Prodej", "Brno-venkov")

    def test_fotky_jen_z_vlastni_galerie(self):
        photos = self.d["photos"]
        assert len(photos) == 25 == len(set(photos))
        assert photos[0] == "https://www.realitycechy.cz/foto/hq/21218000/21218475/21218475_01.webp"
        assert all("/21218475/" in p for p in photos)
        # podobné nabídky pod detailem fotky na stránce mají – do inzerátu nepatří
        assert "/21171384/" in self.html and "/21208555/" in self.html

    def test_cena_obec_gps(self):
        assert self.d["price"] == 5_697_000
        assert self.d["municipality"] == "Ivančice (Budkovice)"
        assert self.d["district"] == "Brno-venkov"
        assert self.d["latitude"] == pytest.approx(49.0724646)
        assert self.d["longitude"] == pytest.approx(16.3462575)

    def test_cizi_fotka_v_galerii_se_zahodi(self):
        html = ("<html><body><h1 class='entry-title'>Dům</h1><ul>"
                "<li data-fancybox='gallery' data-src='/foto/hq/21218000/21218475/21218475_01.webp'></li>"
                "<li data-fancybox='gallery' data-src='/foto/hq/21172000/21171384/21171384_01.webp'></li>"
                "</ul></body></html>")
        d = self.scraper.parse_detail_page(html, _item(self.URL, "21218475"), "Dům", "Prodej", "Brno-venkov")
        assert d["photos"] == ["https://www.realitycechy.cz/foto/hq/21218000/21218475/21218475_01.webp"]


# ── dvoufázový běh nad podvrženým webem a DB ─────────────────────────────────

LIST_PATH = "/nemovitosti/prodej-domu/"
BV_LIST = (LIST_PATH, "Dům", "Prodej", "Brno-venkov", DISTRICT_IDS["Brno-venkov"])
BRECLAV_LIST = (LIST_PATH, "Dům", "Prodej", "Břeclav", DISTRICT_IDS["Břeclav"])

DETAIL_HTML = ("<html><body><h1 class='entry-title'>Prodej rodinného domu</h1><table class='detail_table_pravy'>"
               "<tr><th>Cena:</th><td>5 000 000 Kč</td></tr></table></body></html>")


def _card(eid: str, title: str = "Prodej rodinného domu", loc: str = "Ivančice", price: str = "5 000 000 Kč") -> str:
    return (f'<article class="rc-card rc-card--2x1" data-id="{eid}"><div class="rc-card__slides">'
            f'<a class="rc-card__slide"><img src="/foto/mq/1/{eid}/{eid}_01.webp"></a></div>'
            f'<h2 class="rc-card__title"><a href="/nemovitost/prodej/dum/{eid}">{title}</a></h2>'
            f'<div class="rc-card__loc">{loc}</div>'
            f'<span class="rc-card__price"><span class="rc-card__price-nw">{price}</span></span></article>')


def _list_html(cards: List[str], total: Optional[int], has_next: bool = False) -> str:
    count = f'<p class="rc-toolbar__count">{total} výsledků</p>' if total is not None else ""
    nxt = '<a href="?vp-page=99" class="paginator__next">Další</a>' if has_next else ""
    return f'<html><body>{count}<div data-rc-cards>{"".join(cards)}</div>{nxt}</body></html>'


def _site(total: int, per_page: int = 20, prefix: str = "9", **card_kw: Any) -> Dict[int, str]:
    """Výpis o `total` nabídkách po `per_page` kartách; id = prefix + pořadí (900001…)."""
    ids = [f"{prefix}{n:05d}" for n in range(1, total + 1)]
    pages: Dict[int, str] = {}
    for page, start in enumerate(range(0, total, per_page), start=1):
        chunk = ids[start:start + per_page]
        pages[page] = _list_html([_card(i, **card_kw) for i in chunk], total, has_next=start + per_page < total)
    return pages


class _FakeDb:
    def __init__(self, known: Optional[Dict[str, Optional[float]]] = None) -> None:
        self.known = known or {}
        self.types: Dict[str, str] = {}         # uložený typ z detailu (House, Commercial…)
        self.inactive: set = set()               # stažené inzeráty
        self.skips: Dict[str, str] = {}          # scrape_skips: external_id → důvod
        self.touched: List[Tuple[str, Optional[str], Optional[str]]] = []
        self.deactivated: List[str] = []
        self.reasons: Dict[str, Optional[str]] = {}
        self.remembered: List[Tuple[str, str]] = []
        self.kept_seen = 0

    async def get_known_listings(self, source_code: str) -> Dict[str, Dict[str, Any]]:
        assert source_code == "REALITYCECHY"
        return {external_id: {"price": price, "property_type": self.types.get(external_id, "House"),
                              "is_active": external_id not in self.inactive}
                for external_id, price in self.known.items()}

    async def get_skip_reasons(self, source_code: str) -> Dict[str, str]:
        return dict(self.skips)

    async def remember_skipped(self, source_code: str, external_ids: List[str], reason: str) -> None:
        self.remembered.extend((external_id, reason) for external_id in external_ids)

    async def touch_listings(self, source_code: str, items: List[Tuple[str, Optional[str], Optional[str]]]) -> int:
        self.touched.extend(items)
        return len(items)

    async def mark_active_seen(self, source_code: str) -> int:
        self.kept_seen += 1
        return len(self.known)

    async def deactivate_listing(self, source_code: str, external_id: str, reason: Optional[str] = None) -> bool:
        self.deactivated.append(external_id)
        self.reasons[external_id] = reason
        return True


class _FakeRealityCechy(RealityCechyScraper):
    """Scraper s podvrženým webem a ukládáním – žádné HTTP, žádná DB."""

    def __init__(self, pages: Dict[int, Optional[str]], lists: Optional[list] = None,
                 details: Optional[Dict[str, str]] = None) -> None:
        super().__init__(lists=lists or [BV_LIST])
        self.pages = pages
        self.details = details or {}
        self.requested: List[str] = []
        self.saved: List[Dict[str, Any]] = []
        self.detail_limit: Optional[int] = None    # „čas" vyprší po tolika detailech
        self.list_limit: Optional[int] = None      # … nebo po tolika stránkách výpisu
        self.rejected: set = set()                 # external_id, které upsert vyřadí filtrem

    async def _fetch(self, url: str) -> str:
        self.requested.append(url)
        if "/nemovitost/" in url:
            return self.details.get(url.rsplit("/", 1)[-1], DETAIL_HTML)
        page = int(url.split("vp-page=")[1].split("&")[0]) if "vp-page=" in url else 1
        # za poslední stránkou portál vrací znovu poslední stránku
        html = self.pages.get(page, self.pages.get(max(self.pages)) if page > max(self.pages) else None)
        if html is None:
            raise RuntimeError(f"HTTP 503 on page {page}")
        return html

    def out_of_time(self, started: float, now: float) -> bool:
        if self.list_limit is not None and len(self.list_requests) >= self.list_limit:
            return True
        return self.detail_limit is not None and len(self.detail_requests) >= self.detail_limit

    async def _save_listing(self, listing: Dict[str, Any]) -> Optional[str]:
        self.saved.append(listing)
        return None if listing["external_id"] in self.rejected else f"uuid-{listing['external_id']}"

    @property
    def list_requests(self) -> List[str]:
        return [u for u in self.requested if "/nemovitosti/" in u]

    @property
    def detail_requests(self) -> List[str]:
        return [u for u in self.requested if "/nemovitost/" in u]


@pytest.fixture
def fake_db(monkeypatch):
    db = _FakeDb()
    monkeypatch.setattr(realitycechy_scraper, "get_db_manager", lambda: db)
    return db


@pytest.fixture(autouse=True)
def _bez_cekani(monkeypatch):
    async def _no_sleep(_seconds: float) -> None:
        return None

    monkeypatch.setattr(realitycechy_scraper.asyncio, "sleep", _no_sleep)


class TestRealityCechyBeh:
    async def test_plny_beh_projde_vsechny_stranky_a_ne_dal(self, fake_db):
        scraper = _FakeRealityCechy(_site(45))
        seen = await scraper.run(full_rescan=True)

        assert len(scraper.list_requests) == 3   # 45 nabídek po 20 = 3 stránky
        assert "vp-page" not in scraper.list_requests[0]
        assert len(scraper.saved) == 45 == seen
        assert scraper.lists_complete is True
        assert fake_db.kept_seen == 0
        assert all(listing["district"] == "Brno-venkov" for listing in scraper.saved)

    async def test_vypis_delsi_nez_20_stranek_se_projde_cely(self, fake_db):
        scraper = _FakeRealityCechy(_site(503))
        await scraper.run(full_rescan=True)
        assert len(scraper.list_requests) == 26
        assert scraper.lists_complete is True

    async def test_znamy_se_jen_oznaci_detail_jen_novy_a_zmena_ceny(self, fake_db):
        fake_db.known = {"900001": 5_000_000.0, "900002": 4_000_000.0, "900003": None}
        scraper = _FakeRealityCechy(_site(45))
        seen = await scraper.run(full_rescan=True)

        assert [t[0] for t in fake_db.touched] == ["900001", "900002", "900003"]
        assert fake_db.touched[0] == ("900001", "Ivančice", "Brno-venkov")
        fetched = [u.rsplit("/", 1)[-1] for u in scraper.detail_requests]
        assert "900001" not in fetched              # stejná cena – detail netřeba
        assert fetched[:2] == ["900002", "900003"]  # změna ceny napřed
        assert len(fetched) == 44
        assert seen == 3 + 42   # viděné ve výpisu: 3 známé + 42 nově uložených

    async def test_smazany_detail_se_neulozi_a_znamy_se_deaktivuje(self, fake_db):
        fake_db.known = {"900002": 4_000_000.0}
        deleted = _load("detail_smazany.html")
        scraper = _FakeRealityCechy(_site(3), details={"900002": deleted, "900003": deleted})
        seen = await scraper.run(full_rescan=True)

        assert [s["external_id"] for s in scraper.saved] == ["900001"]
        assert fake_db.deactivated == ["900002"]    # nový smazaný v DB není, nic k deaktivaci
        assert scraper.deleted_count == 2
        assert seen == 1 + 1

    async def test_chyba_stranky_vypisu_nic_nedeaktivuje(self, fake_db):
        pages = _site(45)
        pages[2] = None
        scraper = _FakeRealityCechy(pages)
        seen = await scraper.run(full_rescan=True)

        assert len(scraper.list_requests) == 2      # po chybě se dál nestránkuje
        assert scraper.lists_complete is False
        assert fake_db.kept_seen == 1               # aktivní zůstanou viděné
        assert seen == 20

    async def test_portal_ignoruje_strankovani_vypis_je_neuplny(self, fake_db):
        page1 = _site(100)[1]
        scraper = _FakeRealityCechy({1: page1, 2: page1, 3: page1, 4: page1, 5: page1})
        await scraper.run(full_rescan=True)

        assert len(scraper.list_requests) == 2      # 2. stránka nepřinesla nic nového
        assert scraper.lists_complete is False      # 20 ze 100 – deaktivovat se nesmí
        assert fake_db.kept_seen == 1

    async def test_bez_poctu_vysledku_stranku_ridi_odkaz_dalsi(self, fake_db):
        pages = {n: html.replace("<p class=\"rc-toolbar__count\">45 výsledků</p>", "") for n, html in _site(45).items()}
        scraper = _FakeRealityCechy(pages)
        await scraper.run(full_rescan=True)
        assert len(scraper.list_requests) == 3
        assert scraper.lists_complete is True

    async def test_prazdny_vypis_je_uplny(self, fake_db):
        scraper = _FakeRealityCechy({1: _list_html([], total=0)})
        assert await scraper.run(full_rescan=True) == 0
        assert scraper.lists_complete is True

    async def test_neznama_stranka_vypisu_neni_prazdny_vypis(self, fake_db):
        scraper = _FakeRealityCechy({1: "<html><body>Chyba serveru</body></html>"})
        assert await scraper.run(full_rescan=True) == 0
        assert scraper.lists_complete is False

    async def test_inkrementalni_beh_bere_jen_prvni_stranky(self, fake_db):
        scraper = _FakeRealityCechy(_site(200))
        await scraper.run(full_rescan=False)
        assert len(scraper.list_requests) == RealityCechyScraper.INCREMENTAL_LIST_PAGES
        assert scraper.lists_complete is True
        assert fake_db.kept_seen == 0

    async def test_vycerpany_rozpocet_zastavi_detaily_vypis_zustava_uplny(self, fake_db):
        fake_db.known = {"900001": 5_000_000.0}
        scraper = _FakeRealityCechy(_site(45))
        scraper.detail_limit = 10
        seen = await scraper.run(full_rescan=True)

        assert len(scraper.detail_requests) == 10
        # známé jsou obnovené už z výpisu, nestažené nové v DB nejsou – deaktivace je bezpečná
        assert scraper.lists_complete is True
        assert seen == 1 + 10

    async def test_vycerpany_rozpocet_ve_vypisech_nic_nedeaktivuje(self, fake_db):
        scraper = _FakeRealityCechy(_site(45))
        scraper.list_limit = 2
        await scraper.run(full_rescan=True)

        assert len(scraper.list_requests) == 2
        assert scraper.lists_complete is False
        assert fake_db.kept_seen == 1

    async def test_co_by_filtr_zahodil_se_nestahuje(self, fake_db):
        pages = {1: _list_html([_card("1"), _card("2", price="15 000 000 Kč")], total=2)}
        breclav = {1: _list_html([_card("3", loc="Mikulov"), _card("4", loc="ulice Sobotní, Valtice")], total=2)}

        class _TwoLists(_FakeRealityCechy):
            async def _fetch(self, url: str) -> str:
                if "/nemovitosti/" in url:
                    self.requested.append(url)
                    return (breclav if str(DISTRICT_IDS["Břeclav"]) in url else pages)[1]
                return await super()._fetch(url)

        scraper = _TwoLists(pages, lists=[BV_LIST, BRECLAV_LIST])
        await scraper.run(full_rescan=True)
        assert sorted(u.rsplit("/", 1)[-1] for u in scraper.detail_requests) == ["1", "3"]


BV_LAND_LIST = ("/nemovitosti/prodej-pozemku/", "Pozemek", "Prodej", "Brno-venkov", DISTRICT_IDS["Brno-venkov"])


class TestRealityCechyZnameMimoFiltry:
    """7. 11. 2026: 35 známých inzerátů neprošlo filtry na kartě výpisu – každou noc se jen označily
    jako viděné a zůstaly aktivní se starou cenou. 17 z nich byla skutečně mimo limit, 18 komerční
    pozemky a rekreační objekt, které limit pozemků a domů nemají (typ určil detail)."""

    async def test_znamy_nad_cenovy_limit_se_deaktivuje_bez_detailu(self, fake_db):
        fake_db.known = {"1": None, "2": 5_000_000.0}
        pages = {1: _list_html([_card("1", price="12 500 000 Kč"), _card("2")], total=2)}
        scraper = _FakeRealityCechy(pages)
        seen = await scraper.run(full_rescan=True)

        assert fake_db.deactivated == ["1"]
        assert fake_db.reasons["1"] == "out of search filters (list card)"
        assert [t[0] for t in fake_db.touched] == ["2"]   # mimo filtry se jako viděný neoznačí
        assert scraper.detail_requests == []
        assert scraper.retired_count == 1
        assert seen == 1

    async def test_komercni_pozemek_ve_vypisu_pozemku_limit_nema(self, fake_db):
        fake_db.known = {"1": 18_490_000.0, "2": 6_590_000.0}
        fake_db.types = {"1": "Commercial", "2": "Land"}
        pages = {1: _list_html([_card("1", title="Prodej stavebního pozemku", price="18 490 000 Kč"),
                                _card("2", title="Stavební pozemek 608 m2", price="6 590 000 Kč")], total=2)}
        scraper = _FakeRealityCechy(pages, lists=[BV_LAND_LIST])
        await scraper.run(full_rescan=True)

        assert [t[0] for t in fake_db.touched] == ["1"]
        assert fake_db.deactivated == ["2"]

    async def test_karta_bez_obce_znamy_breclav_nedeaktivuje(self, fake_db):
        """Záloha ld+json (karty bez obce a ceny) nesmí shodit celý částečný okres Břeclav."""
        fake_db.known = {"21075762": 7_662_900.0}
        ld = ('<script type="application/ld+json">{"@graph": [{"@type": "ItemList", "itemListElement": '
              '[{"url": "https://www.realitycechy.cz/nemovitost/prodej/dum/21075762", "name": "Prodej domu"}]}]}'
              '</script>')
        scraper = _FakeRealityCechy({1: f"<html><body>{ld}</body></html>"}, lists=[BRECLAV_LIST])
        await scraper.run(full_rescan=True)

        assert fake_db.deactivated == []
        assert [t[0] for t in fake_db.touched] == ["21075762"]

    async def test_detail_vyradi_filtrem_znamy_deaktivovat_oba_zapamatovat(self, fake_db):
        fake_db.known = {"900001": 4_000_000.0}
        scraper = _FakeRealityCechy(_site(2))
        scraper.rejected = {"900001", "900002"}
        seen = await scraper.run(full_rescan=True)

        assert [s["external_id"] for s in scraper.saved] == ["900001", "900002"]
        assert fake_db.deactivated == ["900001"]
        assert fake_db.reasons["900001"] == "out of search filters (detail)"
        assert fake_db.remembered == [("900001", realitycechy_scraper.SKIP_FILTERED),
                                      ("900002", realitycechy_scraper.SKIP_FILTERED)]
        assert seen == 1     # známý obnovený z výpisu; vyřazený nový se nepočítá

    async def test_zapamatovany_vyrazeny_detail_se_nestahuje(self, fake_db):
        fake_db.known = {"900001": 5_000_000.0}
        fake_db.skips = {"900001": realitycechy_scraper.SKIP_FILTERED, "900002": realitycechy_scraper.SKIP_FILTERED}
        scraper = _FakeRealityCechy(_site(3))
        await scraper.run(full_rescan=True)

        assert [u.rsplit("/", 1)[-1] for u in scraper.detail_requests] == ["900003"]
        assert fake_db.touched == []
        assert fake_db.deactivated == ["900001"]
        assert fake_db.reasons["900001"] == realitycechy_scraper.SKIP_FILTERED

    async def test_stazeny_znovu_ve_vypisu_dostane_detail_misto_videno(self, fake_db):
        fake_db.known = {"900001": 5_000_000.0, "900002": 5_000_000.0}
        fake_db.inactive = {"900001"}
        scraper = _FakeRealityCechy(_site(2))
        seen = await scraper.run(full_rescan=True)

        assert [t[0] for t in fake_db.touched] == ["900002"]
        assert [u.rsplit("/", 1)[-1] for u in scraper.detail_requests] == ["900001"]
        assert [s["external_id"] for s in scraper.saved] == ["900001"]
        assert seen == 2

    async def test_smazany_ma_duvod_v_logu(self, fake_db):
        fake_db.known = {"900001": 4_000_000.0}
        scraper = _FakeRealityCechy(_site(1), details={"900001": _load("detail_smazany.html")})
        await scraper.run(full_rescan=True)
        assert fake_db.reasons == {"900001": "deleted on portal (soft 404)"}


class TestRealityCechyRozdeleniPodleFiltru:
    @staticmethod
    def _item(external_id: str, **kw: Any) -> Dict[str, Any]:
        base = {"external_id": external_id, "title": "Prodej pozemku", "municipality": "Kuřim",
                "price": 17_800_000.0, "property_type": "Pozemek", "offer_type": "Prodej", "district": "Brno-venkov"}
        base.update(kw)
        return base

    def test_ulozeny_typ_ma_prednost_pred_typem_vypisu(self):
        items = [self._item("1"), self._item("2"), self._item("3")]
        known = {"1": 17_800_000.0, "2": None}
        in_scope, out = RealityCechyScraper.split_by_filters(items, known, {"1": "Commercial", "2": "Land"})
        assert [i["external_id"] for i in in_scope] == ["1"]
        assert [i["external_id"] for i in out] == ["2"]     # nový „3" mimo filtry nikam

    def test_stazeny_se_znovu_overi_detailem(self):
        items = [{"external_id": "1", "price": 5_000_000.0}, {"external_id": "2", "price": 5_000_000.0}]
        known = {"1": 5_000_000.0, "2": 5_000_000.0}
        selected = RealityCechyScraper.select_for_detail(items, known, revalidate={"2"})
        assert [i["external_id"] for i in selected] == ["2"]
