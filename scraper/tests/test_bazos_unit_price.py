"""Bazoš – cena za m² v poli ceny pozemku a plochy s mezerou v tisících (11. 11. 2026).

Příklady jsou z aktivních inzerátů, které audit unit_price ukázal: 22 pozemků za 550–5 500 Kč,
Měnín 924 m² za 55 Kč, Uherčice „Výměra 22 500 m2“ uložená jako 500 m².
"""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.bazos_scraper import BazosScraper

resolve = BazosScraper.resolve_unit_price


class TestBazosCenaZaMetrVPopisu:

    @pytest.mark.parametrize("text,expected", [
        ("Třída energetické náročnosti: (dle ) Cena: 1 400 Kč/m2", [1400.0]),
        ("číslo 5 - 1678m2 - 2.600,- Kč/m2 číslo 17", [2600.0]),
        ("Výměra 22 500 m2, cena 149,- Kč/m2.", [149.0]),
        ("Cena: 5500,- Kč/m2 Realitní kanceláře, prosím, nevolat.", [5500.0]),
        ("cena by činila 1.350,-Kč/m2), a dle územního plánu", [1350.0]),
        ("prodej přímo od majitele cena: 3 000 Kč/m² V případě zájmu", [3000.0]),
        ("Okamžitý převod. Cena za m²: 2 990 Kč PŘI RYCHLÉM JEDNÁNÍ", [2990.0]),
        ("Prodej pozemků v Havířově. Za 1metr čtvereční 3200 Kč ,velikost", [3200.0]),
        ("prodam pozemek 2200metru v lomnicce 1200kc za metr", [1200.0]),
        ("Cena: 55,50 Kč/m2", [55.5]),
    ])
    def test_zapisy_ceny_za_metr(self, text, expected):
        assert BazosScraper.stated_unit_prices(text) == expected

    @pytest.mark.parametrize("text", [
        "Pozemek o výměře 1 514 m2, cena 2 119 600 Kč.",
        "Nájem 150 Kč/měsíc za parkovací místo.",
        "Dům 4+1, 120 m², zahrada 800 m2.",
    ])
    def test_bez_ceny_za_metr(self, text):
        assert BazosScraper.stated_unit_prices(text) == []


class TestBazosPrepocetCenyZaMetr:

    def test_jednotka_z_popisu_vynasobi_vymerou(self):
        assert resolve(1400.0, "Prodej stavebního pozemku, 3014m2", "Cena: 1 400 Kč/m2", "Pozemek", "Prodej",
                       3014.0) == (4_219_600.0, "1 400 Kč/m²")

    def test_orna_puda_za_55_kc(self):
        assert resolve(55.0, "Prodej orné půdy v kú Měnín", "výměře 924 m2. Cena: 55 Kč/m2.", "Pozemek", "Prodej",
                       924.0) == (50_820.0, "55 Kč/m²")

    def test_bez_jednotky_v_popisu_podle_ceny_pod_5_kc_za_metr(self):
        # Havraníky: „Plocha 2210 m². Elektřina je na pozemku.“, v poli ceny 2 600 Kč
        assert resolve(2600.0, "Stavebni pozemek Havraniky", "Plocha 2210 m². Elektřina je na pozemku.",
                       "Pozemek", "Prodej", 2210.0) == (5_746_000.0, "2 600 Kč/m²")

    def test_podil_neni_cena_za_metr(self):
        # cena 13 908 Kč je celková za podíl 394 m², výměra je celého pozemku
        assert resolve(13908.0, "Pole 4 729 m², Podíl 8 % o výměře 394 m²", "spoluvlastnický podíl",
                       "Pozemek", "Prodej", 4729.0) == (13908.0, None)
        assert resolve(9000.0, "Pole 4 729 m²", "Prodáme podíl 1/12 na pozemku", "Pozemek", "Prodej",
                       4729.0) == (9000.0, None)

    def test_celkova_cena_s_jednotkovou_v_popisu_zustava(self):
        assert resolve(2_900_000.0, "Pozemek 1000 m²", "Cena 2 900 000 Kč, tj. 2 900 Kč/m².", "Pozemek", "Prodej",
                       1000.0) == (2_900_000.0, None)

    def test_celkova_cena_se_spatnou_jednotkou(self):
        text = "Celková rozloha pozemku: 1000 m2. Cena pozemku činí 2.990.000,- Kč/m2 (včetně právního servisu)"
        assert resolve(2_990_000.0, "Prodej stavebního pozemku, 1000m2", text, "Pozemek", "Prodej",
                       1000.0) == (2_990_000.0, None)

    def test_bez_vymery_jen_poznamka(self):
        assert resolve(1000.0, "Prodej stavebního pozemku", "Cena: 1 000 Kč/m2", "Pozemek", "Prodej",
                       None) == (None, "1 000 Kč/m²")

    def test_jedna_koruna_je_cena_neuvedena(self):
        assert resolve(1.0, "Komerční pozemek 14 000 m²", "", "Pozemek", "Prodej", 14000.0) == (None, None)
        assert resolve(1.0, "Rodinný dům", "", "Dům", "Prodej", None) == (None, None)

    def test_dum_a_pronajem_se_neprepocitavaji(self):
        assert resolve(150.0, "Pronájem pozemku", "Cena 150 Kč/m2 ročně", "Pozemek", "Pronájem",
                       2000.0) == (150.0, None)
        assert resolve(3500.0, "Dům", "Cena: 3 500 Kč/m2", "Dům", "Prodej", 800.0) == (3500.0, None)

    def test_drahy_maly_pozemek_bez_jednotky_zustava(self):
        # 8 000 Kč za 1 000 m² je 8 Kč/m² – nad hranicí 5 Kč/m², bez jednotky v popisu nic neodhadujeme
        assert resolve(8000.0, "Zahrada", "Zahrada se sadem.", "Pozemek", "Prodej", 1000.0) == (8000.0, None)

    def test_detail_posle_poznamku_k_cene(self):
        html = """
        <div class="drobky"><a href="https://reality.bazos.cz/prodam/">Prodej</a> &gt;
          <a href="https://reality.bazos.cz/prodam/pozemek/">Pozemky</a> &gt; <b>Inzerát č. 225146004</b></div>
        <h1 class="nadpisdetail">Prodej 2 parcel, určených dle územního plánu k bydlení, k.ú. Bantice</h1>
        <div class="popisdetail">Pozemky mají celkovou velikost: 4695m2. Cena: 550 Kč/m2</div>
        <table><tr><td>Cena:</td><td><b><span translate="no">550 Kč</span></b></td></tr></table>
        """
        item = {"external_id": "225146004", "detail_url": "https://reality.bazos.cz/inzerat/225146004/x.php"}
        listing = BazosScraper()._parse_detail_page(html, item)
        assert listing["property_type"] == "Pozemek"
        assert listing["area_land"] == 4695.0
        assert listing["price"] == 2_582_250.0
        assert listing["price_note"] == "550 Kč/m²"

    def test_detail_domu_posle_prazdnou_poznamku(self):
        html = """
        <div class="drobky"><a href="https://reality.bazos.cz/prodam/dum/">Domy</a></div>
        <h1>Rodinný dům 4+1 Šatov</h1><div class="popisdetail">Dům 4+1, zahrada 800 m2.</div>
        <table><tr><td>Cena:</td><td><b>4 990 000 Kč</b></td></tr></table>
        """
        listing = BazosScraper()._parse_detail_page(html, {"external_id": "1", "detail_url": "x"})
        assert listing["price"] == 4_990_000.0
        assert listing["price_note"] is None  # upsert price_note přepíše – stará jednotka nezůstane


class TestBazosPlochySMezerouVTisicich:

    def _land(self, title: str, description: str) -> object:
        return BazosScraper()._extract_areas(title, description, "Pozemek")[1]

    def test_vymera_s_mezerou(self):
        assert self._land("Prodám zemědělskou půdu v k. ú. Uherčice",
                          "Výměra 22 500 m2, cena 149,- Kč/m2.") == 22500.0

    def test_vymera_s_teckou(self):
        assert self._land("Bojanovice, pole 35.449 m², zemědělské využití", "Pole k prodeji.") == 35449.0

    def test_celkova_vymera_pred_dilcimi_parcelami(self):
        text = ("Nabídka zahrnuje 3 parcely, celková výměra 15 995 m². - parcela č. 1180/29 – 7 983 m², 1/9 podíl "
                "- parcela č. 1375/23 – 141 m², 1/9 podíl")
        assert self._land("Pole 15 995 m², Podíl 11 % o výměře 1 777 m²", text) == 15995.0

    def test_cislo_parcely_se_neslije_s_vymerou(self):
        assert self._land("Pozemek na prodej", "Cena dohodou parcelní číslo 901/11 739m2 901/12 12m2") == 739.0

    def test_titulek_s_mezerou(self):
        assert self._land("Pole 18 222 m²", "") == 18222.0

    def test_dispozice_pred_plochou_domu(self):
        built, _ = BazosScraper()._extract_areas("Prodej domu", "Rodinný dům 3+1 120 m² v klidné části.", "Dům")
        assert built == 120.0

    def test_zastavena_plocha_a_nadvori_neni_plocha_domu(self):
        text = ("Celková výměra pozemků činí 3 632 m2 (zastavěná plocha a nádvoří 1 792 m2 + trvalý travní porost "
                "1 840 m2). Dům má sklep.")
        built, land = BazosScraper()._extract_areas("Prodej domu 5+1, pozemek 3 632 m2 - Korolupy", text, "Dům")
        assert (built, land) == (None, 3632.0)

    def test_zahrada_s_mezerou_u_domu(self):
        text = "Velkou předností nemovitosti je zahrada o rozloze přibližně 2 000 m², která navazuje na dům."
        assert BazosScraper()._extract_areas("Prodám RD 4+1 s nádvořím a zahradou", text, "Dům")[1] == 2000.0
