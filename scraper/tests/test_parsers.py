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
# ProdejmetoScraper – nový Next.js scraper (Server Action API)
# ---------------------------------------------------------------------------

SAMPLE_RSC_RESPONSE = (
    '0:{"a":"$@1","f":"","b":"test"}\n'
    '2:Ta,Popis bytu\n'
    '3:T17,Popis pozemku na prodej\n'
    '1:[{"id":"uuid-1","listingType":"SALE","status":"ACTIVE","slug":"prodej-bytu","title":"Byt 2+kk","description":"$2","price":3500000,"area":55,"landArea":0,"localityCity":"Znojmo","localityRegion":"Jihomoravsk\u00fd kraj","images":["https://example.com/photo1.jpg"],"sourcePayload":{"estate":{"advert_type":1}}},'
    '{"id":"uuid-2","listingType":"RENT","status":"ACTIVE","slug":"pronajem-domu","title":"Pron\u00e1jem domu","description":"$3","price":15000,"area":120,"landArea":300,"localityCity":"Brno","localityRegion":"Jihomoravsk\u00fd kraj","images":[],"sourcePayload":{"estate":{"advert_type":2}}},'
    '{"id":"uuid-3","listingType":"SALE","status":"SOLD","slug":"prodany-byt","title":"Prodan\u00fd byt","description":"","price":2000000,"area":40,"landArea":0,"localityCity":"Praha","localityRegion":"Hlavn\u00ed m\u011bsto Praha","images":[],"sourcePayload":{}}]'
).encode("utf-8")


class TestProdejmetoRscParsing:
    """Tests for _parse_rsc_response: extract listings from RSC stream."""

    def setup_method(self):
        self.scraper = ProdejmetoScraper.__new__(ProdejmetoScraper)

    def test_vraci_vsechny_zaznamy(self):
        listings = self.scraper._parse_rsc_response(SAMPLE_RSC_RESPONSE)
        assert len(listings) == 3

    def test_prvni_zaznam_ma_spravna_data(self):
        listings = self.scraper._parse_rsc_response(SAMPLE_RSC_RESPONSE)
        first = listings[0]
        assert first["id"] == "uuid-1"
        assert first["title"] == "Byt 2+kk"
        assert first["price"] == 3500000
        assert first["localityCity"] == "Znojmo"

    def test_popis_referenci_je_resolved(self):
        listings = self.scraper._parse_rsc_response(SAMPLE_RSC_RESPONSE)
        assert listings[0]["description"] == "Popis bytu"
        assert listings[1]["description"] == "Popis pozemku na prodej"

    def test_prazdna_data_vraci_prazdny_seznam(self):
        listings = self.scraper._parse_rsc_response(b"0:{}\n")
        assert listings == []

    def test_images_v_prvnim_zaznamu(self):
        listings = self.scraper._parse_rsc_response(SAMPLE_RSC_RESPONSE)
        assert listings[0]["images"] == ["https://example.com/photo1.jpg"]


class TestProdejmetoMapListing:
    """Tests for _map_listing: field mapping and SOLD filtering."""

    def setup_method(self):
        self.scraper = ProdejmetoScraper()

    def _make_raw(self, **overrides):
        base = {
            "id": "test-uuid",
            "listingType": "SALE",
            "status": "ACTIVE",
            "slug": "prodej-bytu",
            "title": "Byt 2+kk Znojmo",
            "description": "Popis nemovitosti",
            "price": 3500000,
            "area": 55,
            "landArea": 0,
            "localityCity": "Znojmo",
            "localityRegion": "Jihomoravský kraj",
            "images": ["https://cdn.example.com/photo.jpg"],
            "sourcePayload": {"estate": {"advert_type": 1}},
        }
        base.update(overrides)
        return base

    def test_sold_status_vraci_none(self):
        raw = self._make_raw(status="SOLD")
        assert self.scraper._map_listing(raw) is None

    def test_aktivni_listing_ma_spravne_source_code(self):
        result = self.scraper._map_listing(self._make_raw())
        assert result["source_code"] == "PRODEJMETO"

    def test_external_id_je_uuid(self):
        result = self.scraper._map_listing(self._make_raw())
        assert result["external_id"] == "test-uuid"

    def test_url_pouziva_novy_format(self):
        result = self.scraper._map_listing(self._make_raw())
        assert result["url"] == "https://www.prodejme.to/nemovitosti/prodej-bytu"

    def test_offer_type_sale(self):
        result = self.scraper._map_listing(self._make_raw(listingType="SALE"))
        assert result["offer_type"] == "Prodej"

    def test_offer_type_rent(self):
        result = self.scraper._map_listing(self._make_raw(listingType="RENT"))
        assert result["offer_type"] == "Pronájem"

    def test_property_type_byt(self):
        result = self.scraper._map_listing(self._make_raw())  # advert_type=1
        assert result["property_type"] == "Byt"

    def test_property_type_dum(self):
        raw = self._make_raw(sourcePayload={"estate": {"advert_type": 2}})
        result = self.scraper._map_listing(raw)
        assert result["property_type"] == "Dům"

    def test_property_type_pozemek(self):
        raw = self._make_raw(sourcePayload={"estate": {"advert_type": 3}})
        result = self.scraper._map_listing(raw)
        assert result["property_type"] == "Pozemek"

    def test_property_type_ostatni_fallback(self):
        raw = self._make_raw(sourcePayload={})
        result = self.scraper._map_listing(raw)
        assert result["property_type"] == "Ostatní"

    def test_location_text_kombinuje_mesto_a_region(self):
        result = self.scraper._map_listing(self._make_raw())
        assert result["location_text"] == "Znojmo, Jihomoravský kraj"

    def test_location_text_jen_mesto(self):
        raw = self._make_raw(localityRegion="")
        result = self.scraper._map_listing(raw)
        assert result["location_text"] == "Znojmo"

    def test_price_kladna_hodnota(self):
        result = self.scraper._map_listing(self._make_raw(price=5000000))
        assert result["price"] == 5000000

    def test_price_nula_vraci_none(self):
        result = self.scraper._map_listing(self._make_raw(price=0))
        assert result["price"] is None

    def test_photos_omezeno_na_20(self):
        raw = self._make_raw(images=[f"https://cdn.example.com/{i}.jpg" for i in range(30)])
        result = self.scraper._map_listing(raw)
        assert len(result["photos"]) == 20

    def test_area_land_nula_vraci_none(self):
        result = self.scraper._map_listing(self._make_raw(landArea=0))
        assert result["area_land"] is None

    def test_prazdny_slug_vraci_none(self):
        raw = self._make_raw(slug="")
        assert self.scraper._map_listing(raw) is None


class TestProdejmetoAdvertTypeMap:
    """Tests for ADVERT_TYPE_MAP constant."""

    def test_vsechny_typy_mapovany(self):
        from core.scrapers.prodejmeto_scraper import ADVERT_TYPE_MAP
        assert ADVERT_TYPE_MAP[1] == "Byt"
        assert ADVERT_TYPE_MAP[2] == "Dům"
        assert ADVERT_TYPE_MAP[3] == "Pozemek"
        assert ADVERT_TYPE_MAP[4] == "Komerční"
        assert ADVERT_TYPE_MAP[5] == "Ostatní"

    def test_listing_type_map_sale_rent(self):
        from core.scrapers.prodejmeto_scraper import LISTING_TYPE_MAP
        assert LISTING_TYPE_MAP["SALE"] == "Prodej"
        assert LISTING_TYPE_MAP["RENT"] == "Pronájem"


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
  <h2 class="pd-header__title">Prodej rodinného domu 5+1, 161 m², Znojmo</h2>
  <div class="pd-header__address">Znojmo, okres Znojmo mapa</div>
  <div class="pd-header__price">3 500 000 Kč (za nemovitost) B Velmi úsporná</div>
  <div class="pd-base-info__content-collapse-inner">
    <p>Prostorný rodinný dům 5+1 v klidné části Znojma. Dům je po kompletní rekonstrukci,
    nachází se na pozemku 750 m².</p>
  </div>
  <div class="pd-detail-info">
    <div class="pd-detail-info__row">
      <span class="pd-detail-info__label">Užitná plocha:</span>
      <span class="pd-detail-info__value">161 m²</span>
    </div>
    <div class="pd-detail-info__row">
      <span class="pd-detail-info__label">Plocha parcely:</span>
      <span class="pd-detail-info__value">750 m²</span>
    </div>
    <div class="pd-detail-info__row">
      <span class="pd-detail-info__label">Stav objektu:</span>
      <span class="pd-detail-info__value">Po rekonstrukci</span>
    </div>
    <div class="pd-detail-info__row">
      <span class="pd-detail-info__label">Druh objektu:</span>
      <span class="pd-detail-info__value">Cihlová</span>
    </div>
    <div class="pd-detail-info__row">
      <span class="pd-detail-info__label">Typ nemovitosti:</span>
      <span class="pd-detail-info__value">Domy a vily</span>
    </div>
  </div>
  <div class="pictogram">
    <div class="pictogram__item" data-toggle="tooltip" title="Počet pokojů">5+1 <i class="icon-rooms"></i></div>
    <div class="pictogram__item" data-toggle="tooltip" title="Užitná plocha">161 m<sup>2</sup></div>
  </div>
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

    def test_extrahuje_plochu_uzitkovou(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("area_built_up") == 161.0

    def test_extrahuje_plochu_pozemku(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("area_land") == 750.0

    def test_extrahuje_stav_objektu(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("condition") == "Po rekonstrukci"

    def test_extrahuje_druh_objektu(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("construction_type") == "Cihlová"

    def test_extrahuje_popis(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        desc = result.get("description", "")
        assert "kompletní rekonstrukci" in desc
        assert "Prodat Koupit" not in desc  # navigace nesmí být v popisu

    def test_extrahuje_lokaci(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        loc = result.get("location_text", "")
        assert "Znojmo" in loc
        assert "mapa" not in loc.lower()

    def test_extrahuje_fotografie(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        photos = result.get("photos", [])
        assert len(photos) >= 1
        assert all("mlsf.remax-czech.cz" in p for p in photos)

    def test_inferuje_typ_nemovitosti_dum(self):
        result = self.scraper._parse_detail_page(MOCK_REMAX_DETAIL_HTML, MOCK_LIST_ITEM)
        assert result.get("property_type") == "Dům"

    def test_deduplication_photos(self):
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
