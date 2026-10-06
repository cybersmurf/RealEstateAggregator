"""
Unit testy pro UlovDomovScraper – parsování uložené sitemapy nabídek a SSR detailů
(tests/fixtures/ulovdomov/), bez HTTP a DB. Osobní údaje inzerentů jsou ve
fixturách nahrazené zástupnými (Jan Novák, +420000000000, makler@example.com).
"""
import copy
import sys
from pathlib import Path
from typing import Any, Dict

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.ulovdomov_scraper import UlovDomovScraper, BASE_URL, MAX_PHOTOS

FIXTURES = Path(__file__).parent / "fixtures" / "ulovdomov"


def _read(name: str) -> str:
    return (FIXTURES / name).read_text(encoding="utf-8")


def _offer(name: str) -> Dict[str, Any]:
    offer = UlovDomovScraper.parse_detail_page(_read(name))
    assert offer is not None
    return offer


class TestUlovDomovSitemap:

    def setup_method(self):
        self.scraper = UlovDomovScraper()
        self.entries = self.scraper.parse_sitemap(_read("sitemap_offers.xml"))
        self.candidates = self.scraper.select_candidates(self.entries)

    def test_pocet_polozek_bez_duplicit(self):
        assert len(self.entries) == 14
        assert len({e["external_id"] for e in self.entries}) == 14

    def test_polozka_ma_url_slug_a_id(self):
        first = self.entries[0]
        assert first["external_id"] == "3496443"
        assert first["slug"] == "pronajem-usti-nad-labem-predlice-tovarni-garsonka"
        assert first["url"] == f"{BASE_URL}/inzerat/pronajem-usti-nad-labem-predlice-tovarni-garsonka/3496443"

    def test_kandidati_jen_z_obci_nasich_okresu(self):
        assert {c["external_id"] for c in self.candidates} == {
            "5673902", "1470297", "5622283", "5662221", "5680750", "5433124", "5681213",
            "5578960",  # Lešná u Vsetína – stejný slug jako Lesná u Znojma, vyřadí ji až detail
            "5667574",  # Říčany u Prahy – totéž
        }

    def test_kandidati_serazeni_od_nejnovejsiho(self):
        ids = [int(c["external_id"]) for c in self.candidates]
        assert ids == sorted(ids, reverse=True)
        assert ids[0] == 5681213

    def test_praha_a_usti_nejsou_kandidati(self):
        ids = {c["external_id"] for c in self.candidates}
        assert "1835792" not in ids
        assert "3496443" not in ids

    def test_ceska_lipa_neni_obec_ceska(self):
        assert "5156114" not in {c["external_id"] for c in self.candidates}

    def test_spolubydleni_a_garaze_se_nestahuji(self):
        ids = {c["external_id"] for c in self.candidates}
        assert "5194058" not in ids
        assert "5643527" not in ids

    def test_omezeni_na_jeden_okres(self):
        scraper = UlovDomovScraper(districts=["Znojmo"])
        ids = {c["external_id"] for c in scraper.select_candidates(self.entries)}
        assert ids == {"5673902", "1470297", "5680750", "5578960"}

    @pytest.mark.parametrize("slug,expected", [
        ("-znojmo-primetice-postovni-fiveplusrooms", "znojmo"),
        ("prodej-znojmo-primetice-postovni-rodinny-dum", "znojmo"),
        ("pronajem-znojmo-1-kk", "znojmo"),
        ("pronajem-moravsky-krumlov-raksice-n-j-navesnika-threerooms", "moravsky-krumlov"),
        ("-ujezd-u-brna-ujezd-u-brna-nadrazni-3-kk", "ujezd-u-brna"),
        ("-brno-bosonohy-bosonohy-housing", "brno"),
        ("pronajem-brnenec-brnenec-2-kk", None),
        ("pronajem-ceska-lipa-ceska-lipa-na-vysluni-2-kk", None),
        ("-ceska-ceska-hlavni-fourrooms", "ceska"),
        ("pronajem-praha-vysocany-freyova-1-kk", None),
        ("spolubydleni-brno-veveri-kounicova-pokoj", None),
        ("pronajem-brno-zabrdovice-stara-garageparking", None),
        ("", None),
    ])
    def test_obec_ze_slugu(self, slug, expected):
        assert self.scraper.match_municipality(slug) == expected

    def test_prazdna_sitemapa(self):
        assert UlovDomovScraper.parse_sitemap("") == []
        assert self.scraper.select_candidates([]) == []


class TestUlovDomovProdejDomu:

    def setup_method(self):
        self.scraper = UlovDomovScraper()
        self.offer = _offer("detail_dum_znojmo_5673902.html")
        self.listing = self.scraper.normalize_offer(self.offer)

    def test_uklada_se(self):
        assert self.scraper.skip_reason(self.offer) is None

    def test_external_id_a_url(self):
        assert self.listing["source_code"] == "ULOVDOMOV"
        assert self.listing["external_id"] == "5673902"
        assert self.listing["url"] == f"{BASE_URL}/inzerat/prodej-znojmo-primetice-postovni-rodinny-dum/5673902"

    def test_typ_a_nabidka(self):
        assert self.listing["property_type"] == "Dům"
        assert self.listing["offer_type"] == "Prodej"

    def test_cena_a_poznamka(self):
        assert self.listing["price"] == 4749000.0
        assert self.listing["price_note"] is None  # volný text portálu jde do popisu, ne do štítku u ceny
        assert "Poznámka k ceně: včetně poplatků, včetně provize, včetně právního servisu" in self.listing["description"]

    def test_okres_obec_a_adresa(self):
        assert self.listing["district"] == "Znojmo"
        assert self.listing["municipality"] == "Znojmo"
        assert self.listing["location_text"] == "Poštovní, Znojmo - Přímětice, okres Znojmo"

    def test_titulek_s_obci(self):
        assert self.listing["title"] == "Prodej domu 133 m², Znojmo - Přímětice"

    def test_plochy(self):
        assert self.listing["area_built_up"] == 133.0
        assert self.listing["area_land"] == 545.0

    def test_pokoje_bez_dispozice(self):
        assert self.listing["rooms"] == 5
        assert self.listing["disposition"] is None

    def test_stav_a_konstrukce(self):
        assert self.listing["condition"] == "Před rekonstrukcí"
        assert self.listing["construction_type"] == "Cihla"

    def test_gps(self):
        assert self.listing["latitude"] == pytest.approx(48.8814)
        assert self.listing["longitude"] == pytest.approx(16.03521)

    def test_nejvyse_dvacet_fotek(self):
        assert len(self.offer["photos"]) == 26
        assert len(self.listing["photos"]) == MAX_PHOTOS == 20
        assert self.listing["photos"][0] == "https://photo.ulovdomov.cz/3c/c3qVXTRQ3H23729883"
        assert len(set(self.listing["photos"])) == 20

    def test_popis(self):
        assert self.listing["description"].startswith("Prostorný rodinný dům v klidné části Přímětic")

    def test_kontakt_jen_jmeno_a_firma(self):
        assert self.listing["seller_name"] == "Jan Novák"
        assert self.listing["seller_company"] == "Prodejme.to"
        assert "seller_phone" not in self.listing
        assert "seller_email" not in self.listing
        assert "+420000000000" not in str(self.listing)
        assert "makler@example.com" not in str(self.listing)

    def test_datum_zverejneni_se_neuklada(self):
        assert "date_created_source" not in self.listing


class TestUlovDomovPronajemBytu:

    def setup_method(self):
        self.scraper = UlovDomovScraper()
        self.offer = _offer("detail_byt_pronajem_znojmo_1470297.html")
        self.listing = self.scraper.normalize_offer(self.offer)

    def test_typ_a_nabidka(self):
        assert self.scraper.skip_reason(self.offer) is None
        assert self.listing["property_type"] == "Byt"
        assert self.listing["offer_type"] == "Pronájem"

    def test_najem_za_mesic(self):
        assert self.listing["price"] == 15000.0
        assert self.listing["price_note"] is None

    def test_dispozice_a_plocha(self):
        assert self.listing["disposition"] == "2+kk"
        assert self.listing["rooms"] == 2
        assert self.listing["area_built_up"] == 60.0
        assert self.listing["area_land"] is None

    def test_ulice_shodna_s_casti_se_neopakuje(self):
        assert self.listing["location_text"] == "Znojmo - Kasárna, okres Znojmo"
        assert self.listing["title"] == "Pronájem bytu 2+kk 60 m², Znojmo - Kasárna"

    def test_poplatky_a_kauce_v_popisu(self):
        assert "Měsíční náklady: 15 000 Kč" in self.listing["description"]
        assert "Depozit: 15 000 Kč" in self.listing["description"]
        assert self.listing["description"].endswith("Bez provize")

    def test_soukromy_pronajimatel_bez_firmy(self):
        assert self.listing["seller_name"] == "Jan Novák"
        assert self.listing["seller_company"] is None

    def test_chybejici_stav_a_konstrukce(self):
        assert self.listing["condition"] is None
        assert self.listing["construction_type"] is None

    def test_fotky(self):
        assert len(self.listing["photos"]) == 4
        assert all(p.startswith("https://") for p in self.listing["photos"])


class TestUlovDomovPozemek:

    def setup_method(self):
        self.scraper = UlovDomovScraper()
        self.offer = _offer("detail_pozemek_kanice_5622283.html")
        self.listing = self.scraper.normalize_offer(self.offer)

    def test_pozemek_ma_jen_plochu_pozemku(self):
        assert self.scraper.skip_reason(self.offer) is None
        assert self.listing["property_type"] == "Pozemek"
        assert self.listing["area_built_up"] is None
        assert self.listing["area_land"] == 2156.0
        assert self.listing["disposition"] is None
        assert self.listing["rooms"] is None

    def test_okres_brno_venkov(self):
        assert self.listing["district"] == "Brno-venkov"
        assert self.listing["municipality"] == "Kanice"
        assert self.listing["location_text"] == "Kanice, okres Brno-venkov"

    def test_cena(self):
        assert self.listing["price"] == 8920000.0
        assert self.listing["price_note"] is None
        assert "Poznámka k ceně: Včetně služeb a provize RK" in self.listing["description"]

    def test_cena_za_metr_se_prepocita_plochou(self):
        offer = _offer("detail_pozemek_lesna_vsetin_5578960.html")
        listing = self.scraper.normalize_offer(offer)
        assert offer["priceUnit"] == "perSqM"
        assert listing["area_land"] == 1106.0
        assert listing["price"] == 884800.0
        assert listing["price_note"] == "800 Kč/m²"
        assert "Poznámka k ceně: + provize RK" in listing["description"]

    def test_cena_za_metr_bez_plochy_se_neuvadi(self):
        offer = copy.deepcopy(_offer("detail_pozemek_lesna_vsetin_5578960.html"))
        offer["parameters"]["estateArea"] = None
        listing = self.scraper.normalize_offer(offer)
        assert listing["price"] is None
        assert listing["price_note"].startswith("800 Kč/m²")


class TestUlovDomovBytBrno:

    def setup_method(self):
        self.scraper = UlovDomovScraper()
        self.listing = self.scraper.normalize_offer(_offer("detail_byt_brno_5662221.html"))

    def test_brno_mesto_s_mestskou_casti(self):
        assert self.listing["district"] == "Brno-město"
        assert self.listing["municipality"] == "Brno"
        assert self.listing["location_text"] == "Havraní, Brno - Černovice, okres Brno-město"
        assert self.listing["title"] == "Prodej bytu 1+kk 40 m², Brno - Černovice"

    def test_byt_na_prodej(self):
        assert self.listing["property_type"] == "Byt"
        assert self.listing["offer_type"] == "Prodej"
        assert self.listing["price"] == 5790000.0
        assert self.listing["disposition"] == "1+kk"
        assert self.listing["rooms"] == 1
        assert self.listing["area_built_up"] == 40.0
        assert self.listing["area_land"] is None

    def test_stav_a_firma(self):
        assert self.listing["condition"] == "Po rekonstrukci"
        assert self.listing["construction_type"] == "Cihla"
        assert self.listing["seller_company"] == "BRAVIS REALITY s.r.o."


class TestUlovDomovPronajemDomu:

    def setup_method(self):
        self.scraper = UlovDomovScraper()
        self.offer = _offer("detail_dum_pronajem_krumlov_5680750.html")
        self.listing = self.scraper.normalize_offer(self.offer)

    def test_dum_k_pronajmu(self):
        assert self.scraper.skip_reason(self.offer) is None
        assert self.listing["property_type"] == "Dům"
        assert self.listing["offer_type"] == "Pronájem"
        assert self.listing["price"] == 18000.0

    def test_obec_v_okrese_znojmo(self):
        assert self.listing["district"] == "Znojmo"
        assert self.listing["municipality"] == "Moravský Krumlov"
        assert self.listing["location_text"] == "N. J. Návesníka, Moravský Krumlov - Rakšice, okres Znojmo"

    def test_pokoje_a_plochy(self):
        assert self.listing["rooms"] == 3
        assert self.listing["area_built_up"] == 319.0
        assert self.listing["area_land"] == 319.0
        assert self.listing["condition"] == "Velmi dobrý"


class TestUlovDomovFiltry:

    def setup_method(self):
        self.scraper = UlovDomovScraper()

    def test_stejnojmenna_obec_v_jinem_okrese_se_zahodi(self):
        offer = _offer("detail_pozemek_lesna_vsetin_5578960.html")
        assert offer["district"]["name"] == "Vsetín"
        assert self.scraper.skip_reason(offer) == "other_district"

    def test_neaktivni_inzerat_se_zahodi(self):
        offer = _offer("detail_neaktivni_999999.html")
        assert self.scraper.skip_reason(offer) == "status_IMPORT_IGNORED"

    def test_bez_nabidky(self):
        assert self.scraper.skip_reason(None) == "no_offer"

    def test_spolubydleni_a_pronajem_pozemku_nejsou_v_kategoriich(self):
        coliving = copy.deepcopy(_offer("detail_byt_pronajem_znojmo_1470297.html"))
        coliving["offerTypeId"] = "coliving"
        assert self.scraper.skip_reason(coliving) == "other_category"
        land_rent = copy.deepcopy(_offer("detail_pozemek_kanice_5622283.html"))
        land_rent["offerTypeId"] = "rent"
        assert self.scraper.skip_reason(land_rent) == "other_category"

    def test_vlastni_kategorie(self):
        scraper = UlovDomovScraper(categories=[("rent", "flat")])
        assert scraper.skip_reason(_offer("detail_byt_pronajem_znojmo_1470297.html")) is None
        assert scraper.skip_reason(_offer("detail_dum_znojmo_5673902.html")) == "other_category"

    def test_okres_mimo_vyber(self):
        scraper = UlovDomovScraper(districts=["Brno-město"])
        assert scraper.skip_reason(_offer("detail_dum_znojmo_5673902.html")) == "other_district"
        assert scraper.skip_reason(_offer("detail_byt_brno_5662221.html")) is None


class TestUlovDomovPomocne:

    @pytest.mark.parametrize("value,expected", [
        ("133 m2", 133.0),
        ("2 156 m²", 2156.0),
        ("1.106 m2", 1106.0),
        ("39,7 m2", 39.7),
        ("39.7 m2", 39.7),
        ("1.250,5 m2", 1250.5),
        ("0 m2", None),
        ("Ne", None),
        (None, None),
        (85, 85.0),
        (0, None),
    ])
    def test_plocha(self, value, expected):
        assert UlovDomovScraper._area({"usableArea": {"value": value}}, "usableArea") == expected

    def test_plocha_chybejici_parametr(self):
        assert UlovDomovScraper._area({}, "usableArea") is None
        assert UlovDomovScraper._area({"usableArea": None}, "usableArea") is None

    @pytest.mark.parametrize("offer,area,expected", [
        ({"rentalPrice": {"value": 15000}, "priceUnit": "perMonth"}, None, (15000.0, "")),
        ({"rentalPrice": {"value": 4749000}, "priceUnit": "perRealEstate"}, 133.0, (4749000.0, "")),
        ({"rentalPrice": {"value": 1}, "priceUnit": "perRealEstate"}, None, (None, "")),
        ({"rentalPrice": None}, None, (None, "")),
        ({}, None, (None, "")),
        ({"rentalPrice": {"value": 1250}, "priceUnit": "perSqM"}, 1000.0, (1250000.0, "1 250 Kč/m²")),
    ])
    def test_cena(self, offer, area, expected):
        assert UlovDomovScraper._price(offer, area) == expected

    def test_chata_je_druh_domu(self):
        offer = copy.deepcopy(_offer("detail_dum_znojmo_5673902.html"))
        offer["parameters"]["houseType"]["options"][0]["id"] = "chalet"
        assert UlovDomovScraper().normalize_offer(offer)["property_type"] == "Chata"

    def test_url_ze_slugu_kdyz_chybi_absolutni(self):
        offer = copy.deepcopy(_offer("detail_byt_brno_5662221.html"))
        offer["absoluteUrl"] = None
        listing = UlovDomovScraper().normalize_offer(offer)
        assert listing["url"] == f"{BASE_URL}/inzerat/prodej-brno-cernovice-havrani-1-kk/5662221"

    def test_chude_udaje_nespadnou(self):
        listing = UlovDomovScraper().normalize_offer({"id": 1, "offerTypeId": "rent"})
        assert listing["external_id"] == "1"
        assert listing["offer_type"] == "Pronájem"
        assert listing["property_type"] == "Ostatní"
        assert listing["price"] is None
        assert listing["district"] is None
        assert listing["photos"] == []
        assert "latitude" not in listing

    def test_bez_next_data_vyhodi_chybu(self):
        with pytest.raises(ValueError):
            UlovDomovScraper.parse_detail_page("<html><body>nic</body></html>")

    def test_stranka_bez_nabidky_vraci_none(self):
        html = '<script id="__NEXT_DATA__" type="application/json">{"props": {"pageProps": {"offer": {"success": false}}}}</script>'
        assert UlovDomovScraper.parse_detail_page(html) is None
