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


class TestHvRealityRest:
    def test_rest_polozky_maji_url_titulek_a_okres(self):
        import json
        data = json.loads((FIX / "rest_prodej.json").read_text(encoding="utf-8"))
        items = HvRealityScraper.parse_rest_items(data)
        assert len(items) == len(data) == 100
        first = items[0]
        assert first["url"].startswith("https://hvreality.cz/prodej-nemovitosti/")
        assert "&#8211;" not in first["title"] and "–" in first["title"]
        assert first["district_slug"] == "znojmo"

    def test_okresy_mimo_cil_se_poznaji(self):
        items = HvRealityScraper.parse_rest_items([
            {"link": "https://hvreality.cz/prodej-nemovitosti/prodej-domu-krelovice-okres-pelhrimov-prodej-domu-1/", "title": {"rendered": "x"}},
            {"link": "https://hvreality.cz/prodej-nemovitosti/prodej-bytu-brno-okres-brno-venkov-prodej-bytu-2/", "title": {"rendered": "y"}},
            {"link": "https://hvreality.cz/prodej-nemovitosti/prodej-bytu-zdar-okres-zdar-nad-sazavou-prodej-3/", "title": {"rendered": "z"}},
        ])
        assert [i["district_slug"] for i in items] == ["pelhrimov", "brno-venkov", "zdar-nad-sazavou"]


class TestHvRealitySoldAndLocality:
    def test_prodano_se_pozna(self):
        html = (FIX / "detail_prodano.html").read_text(encoding="utf-8")
        result = HvRealityScraper()._parse_detail_page(html, {"url": "https://hvreality.cz/prodej-nemovitosti/prodej-rodinneho-domu-znojmo-okres-znojmo-rd-4-1-s-moznosti-kancelare-ordinace-apod-1068/", "title": ""})
        assert result["is_sold"] is True
        assert result["municipality"] == "Znojmo"
        assert result["district"] == "Znojmo"

    def test_aktivni_neni_prodano(self):
        html = (FIX / "detail_krelovice.html").read_text(encoding="utf-8")
        assert HvRealityScraper._is_sold(BeautifulSoup(html, "html.parser")) is False

    def test_obec_s_pomlckou(self):
        soup = BeautifulSoup('<meta name="description" content="Prodej zahrady Nový Šaldorf-Sedlešovice - okres Znojmo, Jihomoravský kraj. Pozemek 2152 m2.">', "html.parser")
        assert HvRealityScraper._parse_locality(soup, "") == ("Nový Šaldorf-Sedlešovice", "Znojmo")

    def test_bez_okresu_vezme_obec_a_okres_ze_slugu(self):
        soup = BeautifulSoup('<meta name="description" content="Prodej bytu 1+kk Brno - Jihomoravský kraj. Užitná plocha 22 m2.">', "html.parser")
        muni, district = HvRealityScraper._parse_locality(soup, "https://hvreality.cz/prodej-nemovitosti/prodej-bytu-1-kk-brno-okres-brno-mesto-prodej-bytu-1-kk-22-m2-brno-1/")
        assert muni == "Brno"
        assert district is None or district == "Brno-město"
