"""HV Reality – obec a okres z meta description (Horák & Vetchý prodávají po celé ČR)."""
from pathlib import Path

from bs4 import BeautifulSoup

from core.scrapers.hvreality_scraper import HvRealityScraper

FIX = Path(__file__).parent / "fixtures" / "hvreality"


class TestHvRealityLocality:
    def test_krelovice_je_okres_pelhrimov(self):
        soup = BeautifulSoup((FIX / "detail_krelovice.html").read_text(encoding="utf-8"), "html.parser")
        muni, district = HvRealityScraper._parse_locality(soup, "https://hvreality.cz/prodej-nemovitosti/prodej-rodinneho-domu-krelovice-okres-pelhrimov-prodej-rodinneho-domu-178-m2-krelovice-1246/")
        assert muni == "Křelovice"
        assert district == "Pelhřimov"

    def test_detail_nedava_znojmo_a_okoli(self):
        html = (FIX / "detail_krelovice.html").read_text(encoding="utf-8")
        result = HvRealityScraper()._parse_detail_page(html, {"url": "https://hvreality.cz/prodej-nemovitosti/prodej-rodinneho-domu-krelovice-okres-pelhrimov-prodej-rodinneho-domu-178-m2-krelovice-1246/", "title": "Prodej rodinného domu 178 m2, pozemek 813 m2 – Křelovice"})
        assert result["district"] == "Pelhřimov"
        assert result["municipality"] == "Křelovice"
        assert result["location_text"] == "Křelovice, okres Pelhřimov"

    def test_meta_s_vicerocnym_nazvem_obce(self):
        soup = BeautifulSoup('<meta name="description" content="Prodej bytu 3+kk Hrušovany nad Jevišovkou - okres Znojmo, Jihomoravský kraj. Plocha 70 m2.">', "html.parser")
        assert HvRealityScraper._parse_locality(soup, "") == ("Hrušovany nad Jevišovkou", "Znojmo")

    def test_fallback_okres_z_url_slugu(self):
        soup = BeautifulSoup("<html></html>", "html.parser")
        assert HvRealityScraper._parse_locality(soup, "https://hvreality.cz/prodej-nemovitosti/prodej-bytu-jihlava-okres-jihlava-prodej-bytu-2-1-55-m2-jihlava-1300/") == (None, "Jihlava")

    def test_bez_informace_vraci_none(self):
        soup = BeautifulSoup("<html></html>", "html.parser")
        assert HvRealityScraper._parse_locality(soup, "https://hvreality.cz/x/") == (None, None)
