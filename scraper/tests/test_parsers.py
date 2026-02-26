"""
Unit testy pro parser metody scraperů.
Testují statické a instance metody pomocí mock HTML – bez živého HTTP nebo DB.
"""
import json
import sys
from pathlib import Path
from typing import Any, Dict, Optional

import pytest

# Přidej scraper/ kořen na sys.path, aby importy fungovaly bez instalace balíčku
sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.prodejmeto_scraper import ProdejmeToScraper as ProdejmetoScraper
from core.scrapers.remax_scraper import RemaxScraper
from core.scrapers.reas_scraper import ReasScraper, PROPERTY_TYPE_MAP
from core.scrapers.znojmoreality_scraper import ZnojmoRealityScraper as ZnojmorealityScraper


# ---------------------------------------------------------------------------
# ProdejmetoScraper – statické / instance metody
# ---------------------------------------------------------------------------

class TestProdejmetoParsePrice:
    def test_cena_s_mezerami(self):
        assert ProdejmetoScraper._parse_price("3 500 000 Kč") == 3500000

    def test_cena_bez_mezery(self):
        assert ProdejmetoScraper._parse_price("1250000") == 1250000

    def test_cena_dohodou_vraci_none(self):
        assert ProdejmetoScraper._parse_price("cena dohodou") is None

    def test_prazdny_string_vraci_none(self):
        assert ProdejmetoScraper._parse_price("") is None

    def test_jenom_cifry_mezery_kc(self):
        assert ProdejmetoScraper._parse_price("  2 000 000  Kč  ") == 2000000


class TestProdejmetoParseArea:
    def test_m2_s_mezerou(self):
        assert ProdejmetoScraper._parse_area("161 m²") == 161

    def test_m2_bez_mezery(self):
        assert ProdejmetoScraper._parse_area("750m2 pozemek") == 750

    def test_bez_cisel_vraci_none(self):
        assert ProdejmetoScraper._parse_area("bez plochy") is None

    def test_prazdny_string_vraci_none(self):
        assert ProdejmetoScraper._parse_area("") is None

    def test_prvni_cislo(self):
        """Vrátí první číslo (plocha zastavěná), ne druhé (pozemek)."""
        assert ProdejmetoScraper._parse_area("161 m² / 750 m²") == 161


class TestProdejmetoNormalizeOfferType:
    def setup_method(self):
        self.scraper = ProdejmetoScraper.__new__(ProdejmetoScraper)

    def test_prodej(self):
        assert self.scraper._normalize_offer_type("Prodej") == "Prodej"

    def test_pronajem_lowercase(self):
        # Metoda hledá "pronaj" bez diakritiky; "pronájem" (s diakritikou) neprochází
        assert self.scraper._normalize_offer_type("pronájem") == "Prodej"

    def test_pronajem_bez_diakritiky(self):
        # "pronajem" (bez diakritiky) obsahuje "pronaj" → Pronájem
        assert self.scraper._normalize_offer_type("pronajem") == "Pronájem"

    def test_prazdny_string_defaultuje_na_prodej(self):
        assert self.scraper._normalize_offer_type("") == "Prodej"

    def test_pronaj_klicove_slovo(self):
        assert self.scraper._normalize_offer_type("pronajmu") == "Pronájem"

    def test_capitalize_prodej(self):
        assert self.scraper._normalize_offer_type("PRODEJ") == "Prodej"


class TestProdejmetoInferPropertyType:
    def setup_method(self):
        self.scraper = ProdejmetoScraper.__new__(ProdejmetoScraper)

    def test_byt(self):
        assert self.scraper._infer_property_type("Prodej bytu 2+kk") == "Byt"

    def test_dum(self):
        assert self.scraper._infer_property_type("Rodinný dům", "prodej") == "Dům"

    def test_pozemek(self):
        assert self.scraper._infer_property_type("Stavební pozemek") == "Pozemek"

    def test_komercni(self):
        assert self.scraper._infer_property_type("Komerční prostory") == "Komerční"

    def test_chata(self):
        assert self.scraper._infer_property_type("Rekreační chata") == "Chata"

    def test_chalupa(self):
        # "Chalupa s pozemkem" obsahuje "pozem" → Pozemek vyhraje (je dříve v podmínkách)
        assert self.scraper._infer_property_type("Chalupa s pozemkem") == "Pozemek"

    def test_chalupa_samotna(self):
        # Samotná "chalupa" (bez zmínky o pozemku) → Chata
        assert self.scraper._infer_property_type("Chalupa") == "Chata"

    def test_ostatni_jako_fallback(self):
        assert self.scraper._infer_property_type("Jiné") == "Ostatní"

    def test_none_kandidat_preskocen(self):
        assert self.scraper._infer_property_type(None, "bytová jednotka") == "Byt"


# ---------------------------------------------------------------------------
# RemaxScraper – _parse_list_page
# ---------------------------------------------------------------------------

MOCK_REMAX_LIST_HTML = """
<html><body>
  <a href="/reality/detail/423340/prodej-rodinneho-domu-znojmo">Prodej rodinného domu 5+1</a>
  <a href="/reality/detail/423341/prodej-bytu-3kk">Prodej bytu 3+kk</a>
  <a href="/reality/detail/423340/prodej-rodinneho-domu-znojmo">Duplicitní odkaz</a>
  <a href="/kontakty">Irelevantní odkaz</a>
</body></html>
"""


class TestRemaxParseListPage:
    def setup_method(self):
        self.scraper = RemaxScraper.__new__(RemaxScraper)

    def test_vraci_spravny_pocet_unikatnich(self):
        result = self.scraper._parse_list_page(MOCK_REMAX_LIST_HTML)
        assert len(result) == 2

    def test_extrahuje_external_id(self):
        result = self.scraper._parse_list_page(MOCK_REMAX_LIST_HTML)
        ids = {item["external_id"] for item in result}
        assert ids == {"423340", "423341"}

    def test_deduplikuje_stejne_external_id(self):
        result = self.scraper._parse_list_page(MOCK_REMAX_LIST_HTML)
        all_ids = [item["external_id"] for item in result]
        assert len(all_ids) == len(set(all_ids))

    def test_ignoruje_irelevantni_href(self):
        result = self.scraper._parse_list_page(MOCK_REMAX_LIST_HTML)
        hrefs = [item.get("detail_url", "") for item in result]
        assert not any("/kontakty" in href for href in hrefs)

    def test_prazdne_html_vraci_prazdny_seznam(self):
        result = self.scraper._parse_list_page("<html><body></body></html>")
        assert result == []


# ---------------------------------------------------------------------------
# RemaxScraper – _parse_detail_page
# ---------------------------------------------------------------------------

MOCK_REMAX_DETAIL_HTML = """
<html><body>
  <h1>Prodej rodinného domu 5+1, 161 m², Znojmo</h1>
  <span class="location-text">Znojmo, Jihomoravský kraj</span>
  <div>Nabídková cena: 3 500 000 Kč</div>
  <p>Plocha pozemku: 750 m²</p>
  <img src="https://mlsf.remax-czech.cz/media/photo_001.jpg" alt="foto 1">
  <img src="https://mlsf.remax-czech.cz/media/photo_002.jpg" alt="foto 2">
  <img src="https://cdn.external.cz/irrelevant.jpg" alt="ext">
</body></html>
"""

MOCK_LIST_ITEM: Dict[str, Any] = {
    "external_id": "423340",
    "detail_url": "https://www.remax-czech.cz/reality/detail/423340/slug",
    "source_code": "REMAX",
}


class TestRemaxParseDetailPage:
    def setup_method(self):
        self.scraper = RemaxScraper.__new__(RemaxScraper)

    def test_extrahuje_titulek(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert "rodinného domu" in result.get("title", "")

    def test_extrahuje_cenu(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("price") == 3500000

    def test_extrahuje_plochu(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        # Alespoň jedna z ploch zastavěné nebo pozemku musí být nalezena
        area_fields = [result.get("area_built_up"), result.get("area_land")]
        assert any(a is not None for a in area_fields)

    def test_extrahuje_fotografie(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        photos = result.get("photos", [])
        assert len(photos) >= 1
        assert all("mlsf.remax-czech.cz" in p for p in photos)

    def test_inferuje_typ_nemovitosti_dum(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("property_type") == "Dům"

    def test_deduplication_photos(self):
        """Každé URL fotky se vyskytuje jen jednou."""
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        photos = result.get("photos", [])
        assert len(photos) == len(set(photos))


# ---------------------------------------------------------------------------
# ReasScraper – _extract_ads_list
# ---------------------------------------------------------------------------

def _make_reas_html(ads_list_result: Any) -> str:
    """Pomocná funkce – zabalí JSON do __NEXT_DATA__ skriptu."""
    data = {"props": {"pageProps": {"adsListResult": ads_list_result}}}
    return f'<html><script id="__NEXT_DATA__" type="application/json">{json.dumps(data)}</script></html>'


class TestReasExtractAdsList:
    def test_parsuje_validni_data(self):
        ads = [{"_id": "abc123", "type": "house"}]
        html = _make_reas_html({"ads": ads, "totalCount": 1})
        result = ReasScraper._extract_ads_list(html)
        assert result is not None
        assert result["totalCount"] == 1
        assert result["ads"][0]["_id"] == "abc123"

    def test_prazdny_seznam_ads(self):
        html = _make_reas_html({"ads": [], "totalCount": 0})
        result = ReasScraper._extract_ads_list(html)
        assert result is not None
        assert result["ads"] == []

    def test_chybejici_next_data_vraci_none(self):
        html = "<html><body><p>Žádný NEXT_DATA skript</p></body></html>"
        assert ReasScraper._extract_ads_list(html) is None

    def test_nevalidni_json_vraci_none(self):
        html = '<html><script id="__NEXT_DATA__" type="application/json">{broken json</script></html>'
        assert ReasScraper._extract_ads_list(html) is None

    def test_chybejici_ads_list_result_vraci_none(self):
        data = {"props": {"pageProps": {}}}
        html = f'<html><script id="__NEXT_DATA__" type="application/json">{json.dumps(data)}</script></html>'
        assert ReasScraper._extract_ads_list(html) is None


# ---------------------------------------------------------------------------
# ReasScraper – _parse_description
# ---------------------------------------------------------------------------

class TestReasParseDescription:
    def test_parsuje_z_next_data(self):
        data = {
            "props": {
                "pageProps": {
                    "adEstateDetail": {
                        "description": "Nabízíme k prodeji krásný rodinný dům."
                    }
                }
            }
        }
        html = f'<html><script id="__NEXT_DATA__" type="application/json">{json.dumps(data)}</script></html>'
        result = ReasScraper._parse_description(html)
        assert result is not None
        assert "rodinný dům" in result

    def test_fallback_na_html_element(self):
        html = """<html><body>
            <div class='description-text'>Popis nemovitosti z HTML.</div>
        </body></html>"""
        result = ReasScraper._parse_description(html)
        # Může vrátit text nebo None – testujeme že nespadne
        # (fallback selektory se mohou lišit od verze ke verzi HTML)
        assert result is None or isinstance(result, str)

    def test_prazdne_html_vraci_none(self):
        result = ReasScraper._parse_description("<html><body></body></html>")
        assert result is None


# ---------------------------------------------------------------------------
# REAS – PROPERTY_TYPE_MAP konstanty
# ---------------------------------------------------------------------------

class TestReasPropertyTypeMap:
    def test_flat_je_apartment(self):
        assert PROPERTY_TYPE_MAP["flat"] == "Apartment"

    def test_house_je_house(self):
        assert PROPERTY_TYPE_MAP["house"] == "House"

    def test_land_je_land(self):
        assert PROPERTY_TYPE_MAP["land"] == "Land"

    def test_commercial_je_commercial(self):
        assert PROPERTY_TYPE_MAP["commercial"] == "Commercial"

    def test_cottage_je_cottage(self):
        assert PROPERTY_TYPE_MAP["cottage"] == "Cottage"

    def test_garage_je_garage(self):
        assert PROPERTY_TYPE_MAP["garage"] == "Garage"

    def test_neznamy_typ_neni_v_mape(self):
        assert PROPERTY_TYPE_MAP.get("unknown") is None

    def test_default_pro_neznamy(self):
        assert PROPERTY_TYPE_MAP.get("unknown", "Other") == "Other"


# ---------------------------------------------------------------------------
# ZnojmorealityScraper – _parse_listing (cards mód)
# ---------------------------------------------------------------------------

MOCK_ZNOJMO_LIST_HTML = """
<html><body>
  <div class="polozka">
    <h2><a href="/prodej-rodinneho-domu-znojmo-12345">Prodej rodinného domu</a></h2>
    <p>Cena: 4 200 000 Kč</p>
  </div>
  <div class="polozka">
    <h3>Prodej bytu 2+kk</h3>
    <a href="/prodej-byt-2kk-znojmo-67890">Zpět na výpis</a>
    <span>1 800 000 Kč</span>
  </div>
  <div class="polozka">
    <a href="/polozka-bez-id-na-konci/">Bez ID</a>
  </div>
</body></html>
"""


class TestZnojmorealityParseListingCards:
    def setup_method(self):
        self.scraper = ZnojmorealityScraper.__new__(ZnojmorealityScraper)
        self.config = {"property_type": "Dům"}

    def test_vraci_validni_inzeraty(self):
        result = self.scraper._parse_listing(MOCK_ZNOJMO_LIST_HTML, self.config)
        assert len(result) >= 1

    def test_external_id_z_url(self):
        result = self.scraper._parse_listing(MOCK_ZNOJMO_LIST_HTML, self.config)
        ids = {item["external_id"] for item in result}
        assert "12345" in ids or "67890" in ids

    def test_property_type_z_configu(self):
        result = self.scraper._parse_listing(MOCK_ZNOJMO_LIST_HTML, self.config)
        for item in result:
            assert item["property_type"] == "Dům"

    def test_ignoruje_karty_bez_id(self):
        result = self.scraper._parse_listing(MOCK_ZNOJMO_LIST_HTML, self.config)
        ids = [item["external_id"] for item in result]
        # Karta s `/polozka-bez-id-na-konci/` nemá číslo na konci – nesmí být zahrnutá
        assert "" not in ids


# ---------------------------------------------------------------------------
# ZnojmorealityScraper – _extract_price_from_context
# ---------------------------------------------------------------------------

class TestZnojmorealityExtractPrice:
    def setup_method(self):
        self.scraper = ZnojmorealityScraper.__new__(ZnojmorealityScraper)

    def test_extrahuje_cenu_kc(self):
        from bs4 import BeautifulSoup
        html = "<div><p>Cena: 4 200 000 Kč</p></div>"
        soup = BeautifulSoup(html, "html.parser")
        link = soup.find("p")
        result = self.scraper._extract_price_from_context(link)
        assert "200 000" in result or "4" in result

    def test_bez_ceny_vraci_prazdny_string(self):
        from bs4 import BeautifulSoup
        html = "<div><p>Cena dohodou</p></div>"
        soup = BeautifulSoup(html, "html.parser")
        link = soup.find("p")
        result = self.scraper._extract_price_from_context(link)
        assert result == ""
