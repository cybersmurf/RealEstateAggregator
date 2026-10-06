"""Znojmo Reality – okres z řádku „Okres", rezervace podle data-estate-state, cena z vlastní karty.

6. 10. 2026: web měl 12 nabídek, u nás jich bylo 6. Detail okres uvádí („Okres: Znojmo"),
ale scraper ho nezapisoval – geografický filtr viděl jen obec a zahodil vše mimo Znojmo.
Rezervované nabídky (5 z 12) se ukládaly bez ceny a bez poznámky.
"""
from pathlib import Path

from core.filters import FilterManager
from core.scrapers.znojmoreality_scraper import ZnojmoRealityScraper

FIX = Path(__file__).parent / "fixtures" / "znojmoreality"
BASE = "https://www.znojmoreality.cz"


def _list(fixture: str, property_type: str) -> dict:
    html = (FIX / fixture).read_text(encoding="utf-8")
    items = ZnojmoRealityScraper()._parse_listing(html, {"property_type": property_type})
    return {item["external_id"]: item for item in items}


def _detail(fixture: str, external_id: str, property_type: str = "Dům", **list_fields) -> dict:
    html = (FIX / fixture).read_text(encoding="utf-8")
    item = {"external_id": external_id, "detail_url": f"{BASE}/x-{external_id}", "title": "",
            "price_text": "", "property_type": property_type, **list_fields}
    return ZnojmoRealityScraper()._parse_detail(html, item)


class TestZnojmoRealityVypis:
    def test_kazda_karta_ma_svou_cenu(self):
        # Dřív dostaly všechny karty první cenu na stránce („9 000 Kč" i u bytu na prodej)
        items = _list("list_byty.html", "Byt")
        assert len(items) == 5
        assert items["732"]["price_text"] == "9 000 Kč"
        assert items["731"]["price_text"] == "3 400 000 Kč"

    def test_rezervovana_karta_nema_cenu_ale_stav(self):
        items = _list("list_domy.html", "Dům")
        assert items["726"]["price_text"] == "9 900 000 Kč"
        assert items["726"]["state"] == ""
        assert items["574"]["price_text"] == ""
        assert items["574"]["state"] == "rezervováno"

    def test_rezervovane_byty_ve_vypisu(self):
        items = _list("list_byty.html", "Byt")
        assert {ext_id for ext_id, item in items.items() if item["state"] == "rezervováno"} == {"727", "728", "717"}


class TestZnojmoRealityDetail:
    def test_dum_na_vesnici_ma_okres_a_obec(self):
        listing = _detail("detail_dum_rezervace_boskovstejn_574.html", "574")
        assert listing["district"] == "Znojmo"
        assert listing["municipality"] == "Boskovštejn"
        assert listing["location_text"] == "Boskovštejn"

    def test_nabidka_z_vesnice_projde_geografickym_filtrem(self):
        listing = _detail("detail_byt_jevisovice_731.html", "731", "Byt")
        assert listing["district"] == "Znojmo"
        assert listing["municipality"] == "Jevišovice"
        assert listing["price"] == 3_400_000
        assert FilterManager().passes_search_filters(listing) is True

    def test_rezervace_ma_stitek_a_drzi_posledni_cenu(self):
        listing = _detail("detail_dum_rezervace_boskovstejn_574.html", "574")
        assert listing["price"] is None
        assert listing["price_note"] == "Rezervace"
        assert listing["keep_last_price"] is True
        assert "sold" not in listing

    def test_rezervovane_nabidce_se_nevnuti_cena_z_vypisu(self):
        listing = _detail("detail_dum_rezervace_boskovstejn_574.html", "574", price_text="9 900 000 Kč")
        assert listing["price"] is None

    def test_volna_nabidka_ma_cenu_a_zadny_stitek(self):
        listing = _detail("detail_dum_znojmo_726.html", "726")
        assert listing["price"] == 9_900_000
        assert listing["municipality"] == "Znojmo"
        assert listing["location_text"] == "Gagarinova, Znojmo"
        assert "price_note" not in listing and "keep_last_price" not in listing and "sold" not in listing

    def test_pronajem_ma_mesicni_cenu(self):
        listing = _detail("detail_pronajem_obchod_725.html", "725", "Komerční")
        assert listing["offer_type"] == "Pronájem"
        assert listing["price"] == 45_000

    def test_prodana_nabidka_se_oznaci_k_deaktivaci(self):
        html = """<html><body><h1 data-estate-state="prodáno">Prodej rodinného domu 120 m², Znojmo</h1>
        <table><tr><td>Cena</td><td><span>prodáno</span></td></tr>
        <tr><td>Lokalita</td><td>Znojmo</td></tr><tr><td>Okres</td><td>Znojmo</td></tr></table></body></html>"""
        item = {"external_id": "1", "detail_url": f"{BASE}/x-1", "title": "", "price_text": "", "property_type": "Dům"}
        listing = ZnojmoRealityScraper()._parse_detail(html, item)
        assert listing["sold"] is True
        assert "price_note" not in listing

    def test_stav_jen_z_karty_vypisu(self):
        html = """<html><body><h1>Pronájem bytu 2+kk 75 m², Dyje</h1>
        <table><tr><td>Lokalita</td><td>Dyje</td></tr><tr><td>Okres</td><td>Znojmo</td></tr></table></body></html>"""
        item = {"external_id": "727", "detail_url": f"{BASE}/x-727", "title": "", "price_text": "",
                "state": "rezervováno", "property_type": "Byt"}
        listing = ZnojmoRealityScraper()._parse_detail(html, item)
        assert listing["price_note"] == "Rezervace"

    def test_bez_radku_cena_se_pouzije_cena_z_karty(self):
        html = """<html><body><h1>Prodej stavebního pozemku 625 m², Dyjákovice</h1>
        <table><tr><td>Lokalita</td><td>Dyjákovice</td></tr><tr><td>Okres</td><td>Znojmo</td></tr></table></body></html>"""
        item = {"external_id": "733", "detail_url": f"{BASE}/x-733", "title": "", "price_text": "800 000 Kč",
                "property_type": "Pozemek"}
        listing = ZnojmoRealityScraper()._parse_detail(html, item)
        assert listing["price"] == 800_000

    def test_cena_na_dotaz_jde_do_poznamky(self):
        html = """<html><body><h1>Prodej rodinného domu 120 m², Znojmo</h1>
        <table><tr><td>Cena</td><td><span>Info v RK</span></td></tr>
        <tr><td>Lokalita</td><td>Znojmo</td></tr><tr><td>Okres</td><td>Znojmo</td></tr></table></body></html>"""
        item = {"external_id": "2", "detail_url": f"{BASE}/x-2", "title": "", "price_text": "", "property_type": "Dům"}
        listing = ZnojmoRealityScraper()._parse_detail(html, item)
        assert listing["price"] is None
        assert listing["price_note"] == "Info v RK"
