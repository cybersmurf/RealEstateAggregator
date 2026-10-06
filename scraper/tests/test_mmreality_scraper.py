"""MM Reality (core/scrapers/mmreality_scraper.py) nad uloženými stránkami v tests/fixtures/mmreality – bez HTTP a DB."""
import sys
from pathlib import Path
from typing import Any, Dict, List

import pytest
import yaml

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.filters import FilterManager
from core.scrapers import mmreality_scraper
from core.scrapers.mmreality_scraper import DEFAULT_SEARCH_CONFIGS, MmRealityScraper

FIXTURES = Path(__file__).parent / "fixtures" / "mmreality"
SETTINGS = Path(__file__).parent.parent / "config" / "settings.yaml"

CONFIG_DOMY_ZNOJMO: Dict[str, Any] = {
    "url": "https://www.mmreality.cz/nemovitosti/prodej/domy/znojmo/",
    "offer_type": "Prodej",
    "property_type": "Dům",
}
CONFIG_DOMY_BRNO_VENKOV: Dict[str, Any] = {
    "url": "https://www.mmreality.cz/nemovitosti/prodej/domy/brno-venkov/",
    "offer_type": "Prodej",
    "property_type": "Dům",
}


def _load(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _settings_configs() -> List[Dict[str, Any]]:
    with open(SETTINGS, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)["scrapers"]["mmreality"]["search_configs"]


@pytest.fixture(scope="module")
def scraper() -> MmRealityScraper:
    return MmRealityScraper()


@pytest.fixture(scope="module")
def domy_znojmo(scraper) -> List[Dict[str, Any]]:
    items, _ = scraper._parse_list_page(_load("list_domy_znojmo.html"))
    return items


class TestMmRealityKonfigurace:
    @pytest.mark.parametrize("url, district", [
        ("https://www.mmreality.cz/nemovitosti/prodej/domy/znojmo/", "Znojmo"),
        ("https://www.mmreality.cz/nemovitosti/prodej/pozemky/brno-venkov/", "Brno-venkov"),
        ("https://www.mmreality.cz/nemovitosti/prodej/byty/brno-mesto/", "Brno-město"),
        ("https://www.mmreality.cz/nemovitosti/prodej/domy/brno-venkov", "Brno-venkov"),
        ("https://www.mmreality.cz/nemovitosti/prodej/domy/brno-venkov/?page=2", "Brno-venkov"),
        ("https://www.mmreality.cz/nemovitosti/prodej/domy/brno/", None),
        ("", None),
    ])
    def test_okres_z_url_vypisu(self, url, district):
        assert MmRealityScraper.district_from_config({"url": url}) == district

    def test_klic_district_ma_prednost_pred_url(self):
        config = {"url": "https://www.mmreality.cz/nemovitosti/prodej/domy/brno/", "district": "Brno-město"}
        assert MmRealityScraper.district_from_config(config) == "Brno-město"

    def test_settings_maji_domy_pozemky_a_byty_pro_znojmo_i_brno_venkov(self):
        combos = {
            (MmRealityScraper.district_from_config(c), c["property_type"], c["offer_type"])
            for c in _settings_configs()
        }
        for district in ("Znojmo", "Brno-venkov"):
            for property_type in ("Dům", "Pozemek", "Byt"):
                assert (district, property_type, "Prodej") in combos

    def test_kazdy_vypis_v_settings_i_ve_vychozim_seznamu_ma_okres(self):
        for config in _settings_configs() + DEFAULT_SEARCH_CONFIGS:
            assert MmRealityScraper.district_from_config(config) in ("Znojmo", "Brno-venkov", "Brno-město"), config["url"]


class TestMmRealityVypis:
    def test_polozky_nesou_okres_obec_a_gps(self, domy_znojmo):
        assert len(domy_znojmo) == 4
        item = next(i for i in domy_znojmo if i["external_id"] == "956928")
        assert item["district"] == "Znojmo"
        assert item["municipality"] == "Božice"
        assert item["latitude"] == pytest.approx(48.8377, abs=1e-4)
        assert item["longitude"] == pytest.approx(16.2890, abs=1e-4)
        assert item["detail_url"] == "https://www.mmreality.cz/nemovitosti/956928"

    def test_vypis_pokracuje_dokud_celkovy_pocet_prevysuje_stazene(self, scraper):
        _, has_next = scraper._parse_list_page(_load("list_domy_znojmo.html"))
        assert has_next is True  # metadata.count = 14

    def test_vypis_brno_venkov_ma_okres_brno_venkov(self, scraper):
        items, _ = scraper._parse_list_page(_load("list_domy_brno_venkov_strana2.html"))
        assert len(items) == 8
        assert {i["district"] for i in items} == {"Brno-venkov"}
        assert "Pohořelice" in {i["municipality"] for i in items}


class TestMmRealityDetail:
    def test_detail_prebira_okres_obec_a_gps_z_vypisu(self, scraper, domy_znojmo):
        item = next(i for i in domy_znojmo if i["external_id"] == "956928")
        result = scraper._parse_detail_page(_load("detail_dum_bozice.html"), item, CONFIG_DOMY_ZNOJMO)
        assert result["district"] == "Znojmo"
        assert result["municipality"] == "Božice"
        assert result["location_text"] == "Božice, okres Znojmo"
        assert result["latitude"] == pytest.approx(48.8377, abs=1e-4)
        assert result["property_type"] == "Dům"
        assert result["offer_type"] == "Prodej"
        assert result["area_built_up"] == 165
        assert result["area_land"] == 466

    def test_cena_je_cena_nabidky_ne_splatka_hypoteky(self, scraper, domy_znojmo):
        result = scraper._parse_detail_page(_load("detail_dum_bozice.html"), domy_znojmo[0], CONFIG_DOMY_ZNOJMO)
        assert result["price"] == 3990000

    def test_u_aukce_se_bere_hlavni_cena(self, scraper, domy_znojmo):
        result = scraper._parse_detail_page(_load("detail_dum_lesna_aukce.html"), domy_znojmo[0], CONFIG_DOMY_ZNOJMO)
        assert result["price"] == 4000000

    def test_bez_okresu_ve_vypisu_rozhodne_url_konfigurace(self, scraper):
        item = {
            "external_id": "950788",
            "detail_url": "https://www.mmreality.cz/nemovitosti/950788",
            "title": "Prodej, Rodinný dům, 190 m², Horní Loučky",
            "img_alt": "Horní Loučky",
        }
        result = scraper._parse_detail_page(_load("detail_dum_bozice.html"), item, CONFIG_DOMY_BRNO_VENKOV)
        assert result["district"] == "Brno-venkov"
        assert "municipality" not in result
        assert "latitude" not in result
        # "Horní Loučky" samo o sobě geografickým filtrem neprojde – okres ano
        assert FilterManager().passes_search_filters(result) is True
        assert FilterManager().passes_search_filters(dict(result, district=None)) is False


class _FakeMmReality(MmRealityScraper):
    """Scraper s podvrženým stahováním a ukládáním – žádné HTTP, žádná DB."""

    def __init__(self) -> None:
        super().__init__(search_configs=[CONFIG_DOMY_BRNO_VENKOV])
        self.requested: List[str] = []
        self.saved: List[Dict[str, Any]] = []

    async def _fetch(self, url: str) -> str:
        self.requested.append(url)
        if "/nemovitosti/prodej/" not in url:
            return _load("detail_dum_bozice.html")
        if "page=" in url:
            return "<html><body></body></html>"  # za poslední stranou výpis nic nevrací
        return _load("list_domy_brno_venkov_strana2.html")

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        self.saved.append(listing)


@pytest.fixture(autouse=True)
def _bez_cekani(monkeypatch):
    async def _no_sleep(_seconds: float) -> None:
        return None

    monkeypatch.setattr(mmreality_scraper.asyncio, "sleep", _no_sleep)


class TestMmRealityBeh:
    async def test_ulozene_inzeraty_maji_okres_vypisu(self):
        scraper = _FakeMmReality()
        total = await scraper.run(full_rescan=True)
        assert total == 8 == len(scraper.saved)
        assert {listing["district"] for listing in scraper.saved} == {"Brno-venkov"}
        assert all(listing.get("municipality") for listing in scraper.saved)
        assert all(listing.get("latitude") for listing in scraper.saved)
