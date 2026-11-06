"""REMAX (core/scrapers/remax_scraper.py) nad uloženými stránkami v tests/fixtures/remax – bez HTTP a DB."""
import sys
from pathlib import Path
from typing import Any, Dict, List

import pytest
from bs4 import BeautifulSoup

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.filters import FilterManager
from core.scrapers import remax_scraper
from core.scrapers.remax_scraper import RemaxScraper

FIXTURES = Path(__file__).parent / "fixtures" / "remax"

# Rezervovanou nabídku se na webu nepodařilo dohledat (filtr „rezervováno" ji ve výpisu
# neoznačuje) – stránka je proto složená podle toho, jak REMAX ukazuje „Prodáno".
REZERVOVANO_HTML = """
<html><body>
  <div class="pd-header">
    <h1 class="h2">Prodej domu 120 m², Hrabětice</h1>
    <h2 class="pd-header__address">ulice Dlouhá, Hrabětice <a href="#mapa">mapa</a></h2>
    <h2 class="pd-header__price">Rezervováno</h2>
  </div>
  <div class="tags mb-4"><span class="tags__item tags__item--reserved">Rezervováno</span></div>
  <p>Hypotéka od 7 450 Kč měsíčně</p>
</body></html>
"""


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _item(**extra: Any) -> Dict[str, Any]:
    item: Dict[str, Any] = {
        "source_code": "REMAX",
        "external_id": "435188",
        "detail_url": "https://www.remax-czech.cz/reality/detail/435188/prodej-domu-120-m2-hrabetice",
    }
    item.update(extra)
    return item


@pytest.fixture(scope="module")
def scraper() -> RemaxScraper:
    return RemaxScraper()


class TestRemaxVypisy:
    def test_kazdy_vypis_ma_okres_odpovidajici_url(self):
        for config in RemaxScraper.SEARCH_CONFIGS:
            url = config["url"]
            if config["district"] == "Znojmo":
                assert "/znojmo/" in url or "%5B3713%5D" in url
            elif config["district"] == "Břeclav":
                assert "/breclav/" in url or "%5B3704%5D" in url
            else:
                assert config["district"] == "Brno-venkov"
                assert "/brno-venkov/" in url or "%5B3703%5D" in url

    def test_breclav_ma_domy_pozemky_byty_a_ostatni(self):
        """7. 10. 2026: Pálava a Novomlýnské nádrže – komerční z Břeclavska nechceme."""
        types = [c["property_type"] for c in RemaxScraper.SEARCH_CONFIGS if c["district"] == "Břeclav"]
        assert sorted(types) == ["Byt", "Dům", "Ostatní", "Pozemek"]

    def test_vypisy_pokryvaji_i_ostatni_a_komercni_v_obou_okresech(self):
        for district in ("Znojmo", "Brno-venkov"):
            types = [c["property_type"] for c in RemaxScraper.SEARCH_CONFIGS if c["district"] == district]
            assert sorted(types) == ["Byt", "Dům", "Komerční", "Ostatní", "Pozemek"]

    def test_vsechny_vypisy_jsou_prodej(self):
        assert {c["offer_type"] for c in RemaxScraper.SEARCH_CONFIGS} == {"Prodej"}
        assert all("pronajem" not in c["url"] and "sale=2" not in c["url"] for c in RemaxScraper.SEARCH_CONFIGS)

    def test_url_stranky_vypisu_podle_adresy_a_podle_filtru(self):
        by_path = "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/"
        assert RemaxScraper._page_url(by_path, 2) == by_path + "?stranka=2"
        by_filter = next(c["url"] for c in RemaxScraper.SEARCH_CONFIGS if "vyhledavani" in c["url"])
        assert RemaxScraper._page_url(by_filter, 2) == by_filter + "&stranka=2"

    def test_prvni_strana_ma_21_polozek_a_pokracuje(self, scraper):
        html = _load("list_domy_znojmo.html")
        items = scraper._parse_list_page(html)
        assert len(items) == 21
        assert not any(i["sold"] for i in items)
        assert RemaxScraper._has_next_page(html) is True

    def test_posledni_strana_nepokracuje_a_oznaci_prodanou_nabidku(self, scraper):
        html = _load("list_domy_znojmo_strana2.html")
        items = scraper._parse_list_page(html)
        assert len(items) == 5
        assert [i["external_id"] for i in items if i["sold"]] == ["438397"]
        assert RemaxScraper._has_next_page(html) is False

    def test_vypis_podle_filtru_ma_stejne_karty(self, scraper):
        html = _load("list_ostatni_znojmo_brno_venkov.html")
        items = scraper._parse_list_page(html)
        assert len(items) == 18
        assert all(i["detail_url"].startswith("https://www.remax-czech.cz/reality/detail/") for i in items)
        assert RemaxScraper._has_next_page(html) is False  # "1-18 z celkem 18"

    def test_bez_textu_o_poctu_se_o_dalsi_strance_nerozhoduje(self):
        assert RemaxScraper._has_next_page("<html><body></body></html>") is None


class TestRemaxDetail:
    def test_okres_z_vypisu_a_obec_z_adresy(self, scraper):
        result = scraper._parse_detail_page(_load("detail_dum_hrabetice.html"), _item(district="Znojmo"))
        assert result["location_text"] == "ulice Dlouhá, Hrabětice"
        assert result["district"] == "Znojmo"
        assert result["municipality"] == "Hrabětice"
        assert result["price"] == 1490000.0
        assert result["property_type"] == "Dům"
        assert result["area_built_up"] == 120.0
        assert result["area_land"] == 604.0

    def test_adresa_bez_okresu_projde_geografickym_filtrem_diky_okresu(self, scraper):
        html = _load("detail_dum_hrabetice.html")
        with_district = scraper._parse_detail_page(html, _item(district="Znojmo"))
        assert FilterManager().passes_search_filters(with_district) is True
        without = scraper._parse_detail_page(html, _item())
        assert "district" not in without
        without.pop("municipality")
        assert FilterManager().passes_search_filters(without) is False

    def test_chata_z_vypisu_ostatni(self, scraper):
        result = scraper._parse_detail_page(
            _load("detail_chata_lancov.html"), _item(district="Znojmo", property_type_hint="Ostatní")
        )
        assert result["property_type"] == "Chata"
        assert result["municipality"] == "Lančov"
        assert result["price"] == 8500000.0

    def test_ubytovaci_zarizeni_neni_byt(self, scraper):
        result = scraper._parse_detail_page(
            _load("detail_ubytovaci_zarizeni_lancov.html"), _item(district="Znojmo", property_type_hint="Komerční")
        )
        assert result["property_type"] == "Komerční"

    def test_aktivni_nabidka_nema_stav(self, scraper):
        html = _load("detail_dum_hrabetice.html")
        assert scraper._detect_status(html) is None
        result = scraper._parse_detail_page(html, _item())
        assert "price_note" not in result and "keep_last_price" not in result

    def test_prodana_nabidka(self, scraper):
        html = _load("detail_prodano_uhercice.html")
        assert scraper._detect_status(html) == "sold"
        # místo ceny je "Prodáno" – žádná částka odjinud ze stránky se nesmí vzít
        assert "price" not in scraper._parse_detail_page(html, _item())

    def test_rezervovana_nabidka_zustava_s_poznamkou(self, scraper):
        assert scraper._detect_status(REZERVOVANO_HTML) == "reserved"
        result = scraper._parse_detail_page(REZERVOVANO_HTML, _item(district="Znojmo"))
        assert result["price_note"] == "Rezervace"
        assert result["keep_last_price"] is True
        assert "price" not in result  # splátka hypotéky není cena

    @pytest.mark.parametrize("address, municipality", [
        ("ulice Dlouhá, Hrabětice", "Hrabětice"),
        ("Višňové, okres Znojmo", "Višňové"),
        ("Strachotice – část obce Micmanice", "Strachotice"),
        ("Hodonice – část obce", "Hodonice"),
        ("Znojmo – část obce Přímětice", "Znojmo"),
        ("ulice Znojemská, Pohořelice", "Pohořelice"),
        ("ulice Vodní, Hrušovany u Brna", "Hrušovany u Brna"),
        ("ulice Bez Obce", None),
        ("", None),
    ])
    def test_obec_z_adresy(self, address, municipality):
        assert RemaxScraper._municipality_from_address(address) == municipality

    @pytest.mark.parametrize("title, type_param, hint, expected", [
        ("prodej domu 120 m², hrabětice", "Domy a vily", "Dům", "Dům"),
        ("prodej chaty / chalupy 230 m², lančov", "Chaty a rekreační objekty", "Ostatní", "Chata"),
        ("prodej ubytovacího zařízení 230 m², lančov", "Hotely, penziony a restaurace", "Komerční", "Komerční"),
        ("prodej ubytovacího zařízení 230 m², lančov", "", "Komerční", "Komerční"),
        ("prodej bytu 2+1 v osobním vlastnictví 52 m²", "Byty", "Byt", "Byt"),
        ("prodej bytu 2+1", "", "Ostatní", "Byt"),
        ("prodej pozemku 992 m², nesvačilka", "", "Ostatní", "Pozemek"),
        ("prodej chaty / chalupy 32 m², doubravník", "", "Ostatní", "Chata"),
        ("prodej garáže, ivančice", "", "Ostatní", "Garáž"),
        ("prodej zemědělského objektu 158 m², rešice", "", "Ostatní", "Komerční"),
        ("prodej vinného sklepa, božice", "", "Ostatní", "Ostatní"),
        ("prodej historického objektu 1200 m², lomnice", "", "Ostatní", "Ostatní"),
    ])
    def test_typ_nemovitosti(self, title, type_param, hint, expected):
        assert RemaxScraper._infer_property_type(title, type_param, hint) == expected


def _bez_parametru(html: str, label: str) -> str:
    """Stránka bez řádku parametru (např. „Celková plocha:")."""
    soup = BeautifulSoup(html, "html.parser")
    for row in soup.select(".pd-detail-info__row"):
        label_el = row.select_one(".pd-detail-info__label")
        if label_el and label_el.get_text(strip=True) == label:
            row.decompose()
    return str(soup)


class TestRemaxCena:
    """6. 11. 2026: „5 326,60 Kč (za m²)" se uložilo jako 60 Kč, „4 369 000,02 Kč" jako 2 Kč
    a další tři pozemky s cenou za m² měly jednotkovou cenu místo celkové."""

    def test_cena_za_m2_u_pozemku_se_prepocte_vymerou(self, scraper):
        result = scraper._parse_detail_page(
            _load("detail_pozemek_za_m2_dolni_vestonice_451334.html"),
            _item(external_id="451334", district="Břeclav", property_type_hint="Pozemek"),
        )
        assert result["property_type"] == "Pozemek"
        assert result["area_land"] == 750.0  # pozemek má výměru jen jako „Celková plocha"
        assert result["price"] == 3_994_950.0  # 5 326,60 × 750
        assert result["price_note"] == "5 326,60 Kč/m²"

    def test_cena_za_m2_bez_vymery_neni_cena_jen_poznamka(self, scraper):
        html = _bez_parametru(_load("detail_pozemek_za_m2_dolni_vestonice_451334.html"), "Celková plocha:")
        result = scraper._parse_detail_page(html, _item(external_id="451334", property_type_hint="Pozemek"))
        assert "area_land" not in result
        assert result.get("price") is None
        assert result["price_note"] == "5 326,60 Kč/m²"

    def test_cena_s_halermi_za_desetinnou_carkou(self, scraper):
        result = scraper._parse_detail_page(
            _load("detail_byt_cena_s_halermi_opatovice_451809.html"),
            _item(external_id="451809", district="Brno-venkov", property_type_hint="Byt"),
        )
        assert result["price"] == 4_369_000.0  # na webu „4 369 000,02 Kč"
        assert "price_note" not in result
        assert result["area_built_up"] == 41.0
        assert "area_land" not in result  # „Celková plocha" bytu není pozemek

    def test_cena_z_hlavicky_ne_z_kalkulacky_ani_podobnych_nemovitosti(self, scraper):
        html = _load("detail_dum_dolni_loucky_443823.html")
        # Stránka nese i ceny podobných nemovitostí a splátku z kalkulačky hypotéky
        for other in ("7\xa0490\xa0000\xa0Kč", "9\xa0900\xa0000\xa0Kč", "260 990 Kč"):
            assert other in html
        result = scraper._parse_detail_page(html, _item(external_id="443823", district="Brno-venkov"))
        assert result["price"] == 8_490_000.0
        assert "price_note" not in result
        assert result["area_built_up"] == 218.0
        assert result["area_land"] == 1054.0

    @pytest.mark.parametrize("text, expected", [
        ("8\xa0490\xa0000\xa0Kč (\t\t\t\tza nemovitost) G Mimořádně nehospodárná", (8_490_000.0, False)),
        ("5\xa0326,60\xa0Kč (\t\t\t\tza m²)", (5326.6, True)),
        ("4\xa0369\xa0000,02\xa0Kč (za nemovitost)", (4_369_000.02, False)),
        ("995 Kč (za m²)", (995.0, True)),
        ("2 500 Kč/m2", (2500.0, True)),
        ("12 000 Kč za měsíc", (12_000.0, False)),
        ("8490000 Kč", (8_490_000.0, False)),
        ("Cena na vyžádání v kanceláři", (None, False)),
        ("Prodáno", (None, False)),
    ])
    def test_castka_a_jednotka_z_textu_ceny(self, text, expected):
        assert RemaxScraper._parse_price_text(text) == expected

    @pytest.mark.parametrize("amount, text", [
        (5326.6, "5 326,60"), (2500.0, "2 500"), (995.0, "995"), (1234567.5, "1 234 567,50"),
    ])
    def test_jednotkova_cena_v_poznamce_cesky(self, amount, text):
        assert RemaxScraper._format_czk(amount) == text


class _FakeRemax(RemaxScraper):
    """Scraper s podvrženým stahováním, ukládáním a deaktivací – žádné HTTP, žádná DB."""

    SEARCH_CONFIGS = [
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/",
            "offer_type": "Prodej", "property_type": "Dům", "district": "Znojmo",
        },
        {
            "url": "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/brno-venkov/",
            "offer_type": "Prodej", "property_type": "Dům", "district": "Brno-venkov",
        },
    ]

    def __init__(self) -> None:
        super().__init__()
        self.requested: List[str] = []
        self.saved: List[Dict[str, Any]] = []
        self.deactivated: List[str] = []

    async def _fetch_page_http(self, url: str) -> str:
        self.requested.append(url)
        if "/reality/detail/" in url:
            return _load("detail_dum_hrabetice.html")
        return _load("list_domy_znojmo_strana2.html")  # 5 karet, z toho 1 prodaná, poslední strana

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        self.saved.append(listing)

    async def _deactivate_listing(self, external_id: str) -> None:
        self.deactivated.append(external_id)


@pytest.fixture(autouse=True)
def _bez_cekani(monkeypatch):
    async def _no_sleep(_seconds: float) -> None:
        return None

    monkeypatch.setattr(remax_scraper.asyncio, "sleep", _no_sleep)


class TestRemaxBeh:
    async def test_run_vraci_soucet_vypisu_ne_nabihajici_citac(self):
        scraper = _FakeRemax()
        total = await scraper.run(full_rescan=True)
        # 2 výpisy × 4 uložené; dřív run() sčítal průběžný součet (4 + 8 = 12)
        assert total == 8 == len(scraper.saved)
        assert scraper.scraped_count == 8

    async def test_inzeraty_nesou_okres_sveho_vypisu(self):
        scraper = _FakeRemax()
        await scraper.run(full_rescan=True)
        assert [listing["district"] for listing in scraper.saved] == ["Znojmo"] * 4 + ["Brno-venkov"] * 4

    async def test_prodana_nabidka_se_deaktivuje_bez_stazeni_detailu(self):
        scraper = _FakeRemax()
        await scraper.run(full_rescan=True)
        assert scraper.deactivated == ["438397", "438397"]
        assert not any("/detail/438397/" in url for url in scraper.requested)
        assert all(listing["external_id"] != "438397" for listing in scraper.saved)

    async def test_za_posledni_stranu_vypisu_se_uz_nepta(self):
        scraper = _FakeRemax()
        await scraper.run(full_rescan=True)
        list_requests = [url for url in scraper.requested if "/reality/detail/" not in url]
        assert len(list_requests) == 2
        assert all(url.endswith("?stranka=1") for url in list_requests)


class TestRemaxFotky:
    """6. 10. 2026: ukládaly se náhledy 350 px a portréty makléřů; ve skupině duplicit pak detail
    ukázal místo fotek ze Sreality miniatury z RE/MAX."""

    def test_nahled_se_prevede_na_plnou_velikost(self):
        from core.scrapers.remax_scraper import RemaxScraper
        assert RemaxScraper.full_size_photo_url(
            "https://mlsf.remax-czech.cz/data//zs/441090/3392950_th350.jpg"
        ) == "https://mlsf.remax-czech.cz/data//zs/441090/3392950.jpg"
        assert RemaxScraper.full_size_photo_url(
            "https://mlsf.remax-czech.cz/data//zs/441090/3392950.jpg"
        ) == "https://mlsf.remax-czech.cz/data//zs/441090/3392950.jpg"

    def test_portret_maklere_a_cizi_obrazky_nejsou_fotky_nemovitosti(self):
        from core.scrapers.remax_scraper import RemaxScraper
        assert RemaxScraper.full_size_photo_url(
            "https://mlsf.remax-czech.cz/data//uzivatele/12864/1576857_1059524_photo_detail_w.jpg") is None
        assert RemaxScraper.full_size_photo_url("https://www.remax-czech.cz/images/logo.svg") is None

    def test_fotky_podobnych_nemovitosti_se_neberou(self):
        from core.scrapers.remax_scraper import RemaxScraper
        own = [f"https://mlsf.remax-czech.cz/data//zs/441090/33929{i}.jpg" for i in range(50, 55)]
        similar = ["https://mlsf.remax-czech.cz/data//zs/447088/3501001.jpg",
                   "https://mlsf.remax-czech.cz/data//zs/442992/3477002.jpg"]
        assert RemaxScraper.own_listing_photos(own[:2] + similar + own[2:]) == own
        assert RemaxScraper.own_listing_photos([]) == []
