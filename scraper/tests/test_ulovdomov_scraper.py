"""
Unit testy pro UlovDomovScraper – parsování uložené sitemapy nabídek a SSR detailů
(tests/fixtures/ulovdomov/), bez HTTP a DB. Osobní údaje inzerentů jsou ve
fixturách nahrazené zástupnými (Jan Novák, +420000000000, makler@example.com).
"""
import copy
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List

import httpx
import pytest
from zoneinfo import ZoneInfo

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers import ulovdomov_scraper
from core.scrapers.ulovdomov_scraper import UlovDomovScraper, BASE_URL, MAX_PHOTOS, SITEMAP_URL

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

    def test_volne_od_ze_stareho_formatu_neni_datum_vlozeni(self):
        assert "date_created_source" not in self.listing
        assert "date_created_source" not in self.scraper.normalize_offer(_offer("detail_byt_brno_5662221.html"))


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
        assert UlovDomovScraper._area({"usableArea": value}, "usableArea") == expected

    def test_plocha_chybejici_parametr(self):
        assert UlovDomovScraper._area({}, "usableArea") is None
        assert UlovDomovScraper._area({"usableArea": None}, "usableArea") is None
        assert UlovDomovScraper._area({"usableArea": False}, "usableArea") is None

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


class TestUlovDomovStazeny:
    """6. 11. 2026: inzerát 5675451 vrací 404 („Tady nikdo nebydlí“), sitemap ho nese dál."""

    def test_stranka_404_je_stazeny_inzerat(self):
        html = _read("detail_404_5675451.html")
        assert UlovDomovScraper.is_not_found_page(html) is True
        assert UlovDomovScraper.parse_detail_page(html) is None

    @pytest.mark.parametrize("name", ["detail_dum_znojmo_5673902.html", "detail_neaktivni_999999.html"])
    def test_detail_neni_stranka_404(self, name):
        assert UlovDomovScraper.is_not_found_page(_read(name)) is False

    def test_bez_next_data_neni_stranka_404(self):
        assert UlovDomovScraper.is_not_found_page("<html><body>nic</body></html>") is False


# Kandidáti sitemapy bez vlastní fixtury: co vrátí jejich detail
HTTP_404_ID = "5681213"
SOFT_404_ID = "5667574"   # HTTP 200, ale stránka Next.js /404
INACTIVE_ID = "5433124"   # status IMPORT_IGNORED
DETAIL_FIXTURES = {
    "5680750": "detail_dum_pronajem_krumlov_5680750.html",
    "5673902": "detail_dum_znojmo_5673902.html",
    "5662221": "detail_byt_brno_5662221.html",
    "5622283": "detail_pozemek_kanice_5622283.html",
    "5578960": "detail_pozemek_lesna_vsetin_5578960.html",
    "1470297": "detail_byt_pronajem_znojmo_1470297.html",
    SOFT_404_ID: "detail_404_5675451.html",
    INACTIVE_ID: "detail_neaktivni_999999.html",
}


class _FakeUlovDomov(UlovDomovScraper):
    """Sitemap a detaily z fixtur; žádné HTTP, žádná DB."""

    def __init__(self, all_404: bool = False) -> None:
        super().__init__()
        self.all_404 = all_404
        self.saved: List[Dict[str, Any]] = []
        self.deactivated: List[str] = []

    async def _fetch(self, url: str) -> str:
        if url == SITEMAP_URL:
            return _read("sitemap_offers.xml")
        offer_id = url.rsplit("/", 1)[1]
        if self.all_404 or offer_id == HTTP_404_ID:
            request = httpx.Request("GET", url)
            raise httpx.HTTPStatusError("404", request=request, response=httpx.Response(404, request=request))
        return _read(DETAIL_FIXTURES[offer_id])

    async def _save_listing(self, listing: Dict[str, Any]) -> None:
        self.saved.append(listing)

    async def _deactivate_listing(self, external_id: str) -> bool:
        self.deactivated.append(external_id)
        return True


@pytest.fixture
def _bez_cekani(monkeypatch):
    async def _no_sleep(_seconds: float) -> None:
        return None

    monkeypatch.setattr(ulovdomov_scraper.asyncio, "sleep", _no_sleep)


@pytest.mark.usefixtures("_bez_cekani")
class TestUlovDomovBehStazene:

    async def test_stazene_se_neulozi_a_deaktivuji(self):
        scraper = _FakeUlovDomov()
        total = await scraper.scrape(max_details=None)
        saved_ids = {listing["external_id"] for listing in scraper.saved}
        assert saved_ids == {"5680750", "5673902", "5662221", "5622283", "1470297"}
        assert total == 5
        assert sorted(scraper.deactivated) == sorted([HTTP_404_ID, SOFT_404_ID, INACTIVE_ID])
        assert scraper.skipped["http_404"] == 1
        assert scraper.skipped["not_found_page"] == 1
        assert scraper.skipped["status_IMPORT_IGNORED"] == 1

    async def test_jiny_okres_se_nedeaktivuje(self):
        scraper = _FakeUlovDomov()
        await scraper.scrape(max_details=None)
        assert scraper.skipped["other_district"] == 1
        assert "5578960" not in scraper.deactivated

    async def test_pojistka_kdyz_vse_vraci_404(self, monkeypatch):
        monkeypatch.setattr(ulovdomov_scraper, "GONE_GUARD_MIN", 2)
        scraper = _FakeUlovDomov(all_404=True)
        assert await scraper.scrape(max_details=None) == 0
        assert scraper.skipped["http_404"] == 9
        assert scraper.deactivated == []

    async def test_vsechny_detaily_spadly_je_chyba_behu(self, monkeypatch):
        """10. 11. 2026: nový formát detailu shodil parser u každého inzerátu a běh se zapsal jako
        „Succeeded, 0“. Teď skončí výjimkou, runner ho zapíše jako Failed i s důvodem."""
        monkeypatch.setattr(ulovdomov_scraper, "PARSE_FAILURE_MIN", 2)

        def _broken(_self: UlovDomovScraper, _offer: Dict[str, Any]) -> Dict[str, Any]:
            raise AttributeError("'list' object has no attribute 'get'")

        monkeypatch.setattr(UlovDomovScraper, "normalize_offer", _broken)
        scraper = _FakeUlovDomov()
        with pytest.raises(RuntimeError, match="formát"):
            await scraper.scrape(max_details=None)
        assert scraper.parse_errors == 5
        # Stažení (404, stav ≠ ACTIVE) se pozná bez parseru dat – ověřeně stažené se deaktivují i tak
        assert sorted(scraper.deactivated) == sorted([HTTP_404_ID, SOFT_404_ID, INACTIVE_ID])


class TestUlovDomovNovyFormat:
    """Detail od 10. 11. 2026: `parameters` = [{name, value}], dispozice `twoPlusKitchenette`,
    druh domu v `disposition`. Fixtury jsou živé stránky téhož dne."""

    def setup_method(self):
        self.scraper = UlovDomovScraper()

    def _listing(self, name: str) -> Dict[str, Any]:
        offer = _offer(name)
        assert isinstance(offer["parameters"], list)
        assert self.scraper.skip_reason(offer) is None
        return self.scraper.normalize_offer(offer)

    def test_dum_stejne_jako_ve_starem_formatu(self):
        new = self._listing("detail_v2_dum_znojmo_5673902.html")
        old = self.scraper.normalize_offer(_offer("detail_dum_znojmo_5673902.html"))
        fields = ("external_id", "property_type", "offer_type", "price", "price_note", "municipality",
                  "district", "location_text", "title", "area_built_up", "area_land", "rooms",
                  "disposition", "condition", "construction_type", "seller_company", "latitude", "longitude")
        assert {f: new[f] for f in fields} == {f: old[f] for f in fields}
        assert new["area_built_up"] == 133.0
        assert new["area_land"] == 545.0
        assert new["rooms"] == 5
        assert len(new["photos"]) == MAX_PHOTOS

    def test_datum_zverejneni(self):
        listing = self._listing("detail_v2_dum_znojmo_5673902.html")
        published = listing["date_created_source"]
        assert published == datetime(2026, 9, 9, 10, 24, 53, tzinfo=ZoneInfo("Europe/Prague"))
        assert published.utcoffset() == timedelta(hours=2)
        assert self._listing("detail_v2_chata_5683880.html")["date_created_source"].date() == date(2026, 10, 21)

    @pytest.mark.parametrize("raw", ["04.05.2027", "", None, 20261021, "2026-13-40T10:00:00", "nesmysl"])
    def test_neplatne_datum_zverejneni(self, raw):
        assert UlovDomovScraper.published_at({"publishedAt": raw}) is None

    def test_datum_zverejneni_v_budoucnosti_se_nebere(self):
        now = datetime(2026, 11, 11, 4, 0, tzinfo=timezone.utc)
        assert UlovDomovScraper.published_at({"publishedAt": "2026-11-15T10:00:00"}, now=now) is None
        assert UlovDomovScraper.published_at({"publishedAt": "2026-11-11T10:00:00"}, now=now) is not None

    def test_url_detailu_ne_zkracena_adresa(self):
        """absoluteUrl nového formátu „/i/<id>/<seo>“ vrací 404 (ověřeno 10. 11. 2026)."""
        offer = _offer("detail_v2_dum_znojmo_5673902.html")
        assert offer["absoluteUrl"].startswith(f"{BASE_URL}/i/")
        listing = self.scraper.normalize_offer(offer)
        assert listing["url"] == f"{BASE_URL}/inzerat/{offer['seo']}/5673902"

    def test_pronajem_bytu(self):
        listing = self._listing("detail_v2_byt_pronajem_brno_5684583.html")
        assert listing["property_type"] == "Byt"
        assert listing["offer_type"] == "Pronájem"
        assert listing["price"] == 18500.0
        assert listing["disposition"] == "2+kk"
        assert listing["rooms"] == 2
        assert listing["area_built_up"] == 47.0
        assert listing["area_land"] is None
        assert listing["condition"] == "Po rekonstrukci"
        assert listing["construction_type"] == "Panel"
        assert listing["district"] == "Brno-město"
        assert listing["location_text"] == "Dunajská, Brno - Starý Lískovec, okres Brno-město"
        assert listing["seller_name"] == "Jan Novák"
        assert "Poznámka k ceně: + energie, + kauce" in listing["description"]

    def test_chata_z_dispozice(self):
        listing = self._listing("detail_v2_chata_5683880.html")
        assert listing["property_type"] == "Chata"
        assert listing["disposition"] is None
        assert listing["rooms"] == 2
        assert listing["condition"] == "Dobrý"
        assert listing["construction_type"] == "Smíšená"

    def test_cena_na_dotaz(self):
        listing = self._listing("detail_v2_dum_cena_na_dotaz_5682915.html")
        assert listing["property_type"] == "Dům"
        assert listing["price"] is None
        assert listing["rooms"] is None  # roomCount „atypical"
        assert listing["area_land"] == 1349.0
        assert listing["condition"] == "Novostavba"
        assert listing["construction_type"] == "Dřevostavba"
        assert "Poznámka k ceně: Cenu Vám rádi sdělíme na vyžádání!" in listing["description"]

    def test_poplatky_a_kauce_v_popisu(self):
        offer = copy.deepcopy(_offer("detail_v2_byt_pronajem_brno_5684583.html"))
        offer["monthlyFeePrice"] = {"value": 3500, "currency": "CZK"}
        offer["parameters"].insert(0, {"name": "priceDeposit", "value": 37000})
        description = self.scraper.normalize_offer(offer)["description"]
        assert "Měsíční náklady: 3 500 Kč" in description
        assert "Depozit: 37 000 Kč" in description

    def test_nulove_poplatky_se_nepisou(self):
        description = self._listing("detail_v2_byt_pronajem_brno_5684583.html")["description"]
        assert "Měsíční náklady" not in description
        assert "Depozit" not in description

    def test_hodnoty_parametru_z_obou_formatu(self):
        new = UlovDomovScraper.parameter_values({"parameters": [
            {"name": "propertyType", "value": "flat"}, {"name": "localityCity", "value": "Brno"},
            {"name": "localityCity", "value": "Jinde"}, {"value": "bez názvu"}, "nesmysl"]})
        assert new == {"propertyType": "flat", "localityCity": "Brno"}
        old = UlovDomovScraper.parameter_values({"parameters": {
            "propertyType": {"title": "Typ", "options": [{"title": "byt", "id": "flat"}]},
            "usableArea": {"title": "Užitná plocha", "value": "40 m2"}, "houseType": None}})
        assert old == {"propertyType": "flat", "usableArea": "40 m2"}
        assert UlovDomovScraper.parameter_values({}) == {}

    @pytest.mark.parametrize("code,expected", [
        ("twoPlusKitchenette", ("2+kk", 2)),
        ("threePlusOne", ("3+1", 3)),
        ("onePlusKk", ("1+kk", 1)),
        ("fivePlusTwo", ("5+2", 5)),
        ("sixAndMore", ("6 a více", 6)),
        ("atypical", ("atypický", None)),
        ("4+1", ("4+1", 4)),
        ("familyHouse", (None, None)),
        ("housing", (None, None)),
        (None, (None, None)),
    ])
    def test_dispozice(self, code, expected):
        assert UlovDomovScraper._disposition(code) == expected

    def test_kod_ze_seznamu(self):
        assert UlovDomovScraper._code({"heating": ["centralGas", "fireplace"]}, "heating") == "centralGas"
        assert UlovDomovScraper._code({"heating": []}, "heating") is None
        assert UlovDomovScraper._code({"floorArea": 0}, "floorArea") is None
