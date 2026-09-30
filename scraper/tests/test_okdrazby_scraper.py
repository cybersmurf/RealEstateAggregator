"""Parser OKdražby.cz (core/scrapers/okdrazby_scraper.py) nad uloženými stránkami v tests/fixtures/okdrazby."""
import sys
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.okdrazby_scraper import OkdrazbyScraper, JMK_DISTRICTS, CDN_URL

FIXTURES = Path(__file__).parent / "fixtures" / "okdrazby"
PRAGUE = ZoneInfo("Europe/Prague")


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


@pytest.fixture(scope="module")
def scraper() -> OkdrazbyScraper:
    return OkdrazbyScraper()


@pytest.fixture(scope="module")
def domy_jmk(scraper):
    return scraper.parse_list_page(_load("list_domy_jmk.html"))


@pytest.fixture(scope="module")
def dum_menin(scraper, domy_jmk):
    items, _ = domy_jmk
    item = next(i for i in items if i["external_id"] == "27828")
    return scraper.parse_detail_page(_load("detail_27828_dum_menin.html"), item, "Dům")


@pytest.fixture(scope="module")
def byt_brno(scraper):
    items, _ = scraper.parse_list_page(_load("list_byty_jmk.html"))
    item = next(i for i in items if i["external_id"] == "27209")
    return scraper.parse_detail_page(_load("detail_27209_byt_brno.html"), item, "Byt")


@pytest.fixture(scope="module")
def pozemek_dobsice(scraper):
    items, _ = scraper.parse_list_page(_load("list_pozemky_jmk.html"))
    item = next(i for i in items if i["external_id"] == "26366")
    return scraper.parse_detail_page(_load("detail_26366_pozemek_dobsice.html"), item, "Pozemek")


class TestOkdrazbyVypis:
    def test_vypis_domu_jmk_ma_8_polozek_bez_dalsi_stranky(self, domy_jmk):
        items, has_next = domy_jmk
        assert len(items) == 8
        assert has_next is False

    def test_polozka_ma_id_url_stav_okres_a_cenu(self, domy_jmk):
        items, _ = domy_jmk
        item = next(i for i in items if i["external_id"] == "27828")
        assert item["url"] == "https://okdrazby.cz/drazba/27828-rod-dum-se-zahradou-v-obci-me"
        assert item["status_code"] == "prepared"
        assert item["district"] == "Brno-venkov"
        assert item["region"] == "Jihomoravský kraj"
        assert item["lowest_submission"] == 4950000
        assert item["start"] == "2026-10-13T11:00:00.000"

    def test_vsechny_polozky_krajskeho_vypisu_jsou_v_jmk(self, domy_jmk):
        items, _ = domy_jmk
        assert {i["district"] for i in items} <= JMK_DISTRICTS

    def test_celostatni_vypis_ma_15_polozek_a_dalsi_stranku(self, scraper):
        items, has_next = scraper.parse_list_page(_load("list_domy_all_page1.html"), page=1)
        assert len(items) == 15
        assert has_next is True
        mimo_jmk = [i for i in items if i["district"] not in JMK_DISTRICTS]
        assert len(mimo_jmk) >= 10

    def test_vypis_bytu_a_pozemku(self, scraper):
        byty, _ = scraper.parse_list_page(_load("list_byty_jmk.html"))
        pozemky, _ = scraper.parse_list_page(_load("list_pozemky_jmk.html"))
        assert len(byty) == 4
        assert len(pozemky) == 12
        assert all(i["url"].startswith("https://okdrazby.cz/drazba/") for i in byty + pozemky)

    def test_prazdne_html_vrati_prazdny_seznam(self, scraper):
        items, has_next = scraper.parse_list_page("<html><body>nic</body></html>")
        assert items == []
        assert has_next is False


class TestOkdrazbyDetailDum:
    def test_zakladni_pole(self, dum_menin):
        assert dum_menin["source_code"] == "OKDRAZBY"
        assert dum_menin["external_id"] == "27828"
        assert dum_menin["url"] == "https://okdrazby.cz/drazba/27828-rod-dum-se-zahradou-v-obci-me"
        assert dum_menin["title"] == "rod. dům se zahradou v obci Měnín"
        assert dum_menin["property_type"] == "Dům"
        assert dum_menin["offer_type"] == "Dražba"

    def test_ceny_drazby(self, dum_menin):
        assert dum_menin["price"] == 4950000
        assert dum_menin["auction_starting_price"] == 4950000
        assert dum_menin["auction_deposit"] == 300000
        assert "Odhadní cena: 7 425 000 Kč" in dum_menin["description"]

    def test_termin_drazby_je_prazsky_cas(self, dum_menin):
        assert dum_menin["auction_date"] == datetime(2026, 10, 13, 11, 0, tzinfo=PRAGUE)
        assert dum_menin["auction_date"].utcoffset() is not None

    def test_lokalita(self, dum_menin):
        assert dum_menin["district"] == "Brno-venkov"
        assert dum_menin["municipality"] == "Měnín"
        assert dum_menin["location_text"] == "Měnín 531, 664 57 Měnín, okres Brno-venkov"
        assert dum_menin["latitude"] == pytest.approx(49.087483)
        assert dum_menin["longitude"] == pytest.approx(16.700892, abs=1e-5)

    def test_fotky_z_cloudfrontu(self, dum_menin):
        assert len(dum_menin["photos"]) == 3
        assert dum_menin["photos"][0].startswith(f"{CDN_URL}/auctions/27828/images/154719")

    def test_popis_obsahuje_text_a_parametry_drazby(self, dum_menin):
        desc = dum_menin["description"]
        assert desc.startswith("- parcela č. 1844/1 o výměře 288 m2")
        assert "Typ dražby: Exekuční dražba (nedobrovolná)" in desc
        assert "Číslo dražby: 121 EX 2343/23-43" in desc
        assert "Termín dražby: 13. 10. 2026 11:00" in desc
        assert "Dražebník: Exekutorský úřad Plzeň - sever" in desc
        assert "<p>" not in desc
        assert len(desc) <= 5000


class TestOkdrazbyDetailBytAPozemek:
    def test_byt_ma_dispozici_a_okres_brno_mesto(self, byt_brno):
        assert byt_brno["property_type"] == "Byt"
        assert byt_brno["disposition"] == "3+1"
        assert byt_brno["rooms"] == 3
        assert byt_brno["district"] == "Brno-město"
        assert byt_brno["municipality"] == "Brno"
        assert byt_brno["price"] == 6998000
        assert byt_brno["auction_deposit"] == 1750000
        assert len(byt_brno["photos"]) == 4

    def test_byt_popis_odkazovany_rsc_textem_je_dohledany(self, byt_brno):
        assert byt_brno["description"].startswith("Draženou nemovitostí je")
        assert "$53" not in byt_brno["description"]

    def test_pozemek_bez_adresy(self, pozemek_dobsice):
        assert pozemek_dobsice["property_type"] == "Pozemek"
        assert pozemek_dobsice["district"] == "Znojmo"
        assert pozemek_dobsice["municipality"] == "Dobšice u Znojma"
        assert pozemek_dobsice["location_text"] == "Dobšice u Znojma, okres Znojmo"
        assert pozemek_dobsice["area_land"] == 793
        assert pozemek_dobsice["price"] == 2819600
        assert pozemek_dobsice["auction_date"] == datetime(2026, 10, 1, 9, 0, tzinfo=PRAGUE)
        assert len(pozemek_dobsice["photos"]) == 3


class TestOkdrazbyNormalizace:
    @pytest.mark.parametrize("status", ["auctioned", "cancelled", "postponed", "noSubmission", "paid"])
    def test_neaktivni_drazba_je_preskocena(self, scraper, status):
        auction = {"id": 1, "name": "x", "statusCode": status, "categories": ["immovable", "houses", "family_house"]}
        assert scraper.normalize_auction(auction, {"url": "https://okdrazby.cz/drazba/1-x"}, "Dům") is None

    @pytest.mark.parametrize("categories, expected", [
        (["immovable", "houses", "cottage"], "Chata"),
        (["immovable", "houses", "villa"], "Dům"),
        (["immovable", "apartments", "2_kk"], "Byt"),
        (["immovable", "land", "gardens"], "Pozemek"),
        (["immovable", "commercial_real_estate", "warehouses"], "Komerční"),
        (["immovable", "other_real_estate", "garage"], "Garáž"),
        ([], "Dům"),
    ])
    def test_typ_nemovitosti_z_kategorii(self, scraper, categories, expected):
        auction = {"id": 2, "name": "x", "statusCode": "prepared", "categories": categories}
        result = scraper.normalize_auction(auction, {}, "Dům")
        assert result["property_type"] == expected

    def test_holandska_drazba_bere_pocatecni_cenu(self, scraper):
        auction = {"id": 3, "name": "x", "statusCode": "ongoing", "categories": ["immovable", "houses", "family_house"],
                   "biddingMethodAttributes": {"initialPrice": "1500000", "lowestSubmission": ""}}
        result = scraper.normalize_auction(auction, {}, "Dům")
        assert result["price"] == 1500000
        assert result["auction_starting_price"] == 1500000

    def test_bez_fotek_pouzije_hlavni_obrazek(self, scraper):
        auction = {"id": 4, "name": "x", "statusCode": "prepared", "categories": [], "imageSnippets": []}
        result = scraper.normalize_auction(auction, {}, "Dům")
        assert result["photos"] == [f"{CDN_URL}/auctions/4/images/main"]
