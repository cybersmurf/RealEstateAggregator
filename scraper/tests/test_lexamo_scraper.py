"""
Unit testy LexamoScraper nad uloženými detaily (tests/fixtures/lexamo/), bez HTTP a DB.
Kontakt makléře je ve fixturách nahrazený zástupným (Jan Novák, makler@example.com).
"""
import sys
from pathlib import Path
from typing import Any, Dict

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.lexamo_scraper import LexamoScraper

FIXTURES = Path(__file__).parent / "fixtures" / "lexamo"

URL_BRNO = ("https://www.lexamo.cz/realman-listing/prodej-bytu-3-kk-brno-okres-brno-mesto-"
            "prodej-bytu-3-kk-79-m2-74m2-zahrady-brno-kamechy-404")
URL_MASOVICE = ("https://www.lexamo.cz/realman-listing/prodej-rodinneho-domu-masovice-okres-znojmo-"
                "prodej-rodinneho-domu-354-m2-masovice-401")
FIXTURE_BY_URL = {URL_BRNO: "detail_byt_brno_kamechy_404.html", URL_MASOVICE: "detail_dum_masovice_401.html"}


class _FakeLexamo(LexamoScraper):
    """Detail z fixtury místo HTTP."""

    async def _fetch(self, client: Any, url: str) -> str:
        return (FIXTURES / FIXTURE_BY_URL[url]).read_text(encoding="utf-8")


async def _detail(url: str) -> Dict[str, Any]:
    result = await _FakeLexamo()._parse_detail(None, url)
    assert result is not None
    return result


class TestLexamoDetail:

    async def test_obec_z_lokality_s_ulici_brno(self):
        d = await _detail(URL_BRNO)
        assert d["location_text"] == "Kamechy, Brno"
        assert d["municipality"] == "Brno"
        assert d["district"] == "Brno-město"

    async def test_obec_z_lokality_bez_ulice(self):
        d = await _detail(URL_MASOVICE)
        assert d["location_text"] == "Mašovice"
        assert d["municipality"] == "Mašovice"
        assert d["district"] == "Znojmo"

    async def test_ostatni_pole_zustavaji(self):
        d = await _detail(URL_MASOVICE)
        assert d["external_id"] == "401"
        assert d["property_type"] == "House"
        assert d["offer_type"] == "Sale"
        assert d["price"] == 1590000.0
        assert d["title"] == "Prodej rodinného domu 354 m², Mašovice"
        assert d["photos"]


class TestLexamoObec:

    @pytest.mark.parametrize("location,url,expected", [
        ("Mašovice", URL_MASOVICE, "Mašovice"),
        ("Dobšická, Znojmo", "https://www.lexamo.cz/realman-listing/pronajem-garaze-znojmo-okres-znojmo-x-407", "Znojmo"),
        ("Mičurinova, Znojmo", "https://www.lexamo.cz/realman-listing/prodej-bytu-1-kk-znojmo-okres-znojmo-x-366", "Znojmo"),
        ("Kamechy, Brno", URL_BRNO, "Brno"),
        ("Hrádecká, Jaroslavice", "https://www.lexamo.cz/realman-listing/pronajem-bytu-1-kk-jaroslavice-okres-znojmo-x-402", "Jaroslavice"),
        ("Vranovská Ves", "https://www.lexamo.cz/realman-listing/prodej-rodinneho-domu-vranovska-ves-okres-znojmo-x-403", "Vranovská Ves"),
        ("Brno-Bystrc", URL_BRNO, "Brno"),
        ("Přímětice, Znojmo, okres Znojmo", "https://www.lexamo.cz/realman-listing/x-znojmo-okres-znojmo-x-1", "Znojmo"),
        ("Znojmo - Přímětice", "https://www.lexamo.cz/realman-listing/x-znojmo-okres-znojmo-x-1", "Znojmo"),
        ("669 02 Znojmo", "https://www.lexamo.cz/realman-listing/x-znojmo-okres-znojmo-x-1", "Znojmo"),
    ])
    def test_posledni_cast_lokality(self, location, url, expected):
        assert LexamoScraper._extract_municipality(location, url) == expected

    @pytest.mark.parametrize("location", ["", "  ", "okres Znojmo", "Užitná plocha 175 m²"])
    def test_bez_obce(self, location):
        assert LexamoScraper._extract_municipality(location, URL_MASOVICE) is None

    def test_obec_kterou_nenese_slug_url_se_neuklada(self):
        # nouzová lokalita z textu stránky („…Znojmo…“) u inzerátu z jiné obce
        assert LexamoScraper._extract_municipality("Znojmo a okolí", URL_MASOVICE) is None
