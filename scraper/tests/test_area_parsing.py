"""Plochy z titulku/popisu (core/area_parsing.py) a jejich doplnění v _enrich_listing_fields.
Titulky jsou reálné, ze zdrojů, které plochy dřív neukládaly vůbec."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.area_parsing import parse_description_land, parse_title_areas, title_offers_land
from core.database import _enrich_listing_fields


class TestParseTitleAreas:
    @pytest.mark.parametrize("title, expected", [
        ("Prodej rodinného domu, 81 m², Tvořihráz", (81, None)),                        # MMR
        ("Prodej rodinné vily, na pozemku 1510 m², Suchohrdly u Znojma", (None, 1510)),  # MMR
        ("RD 2+kk, Rozdrojovice, ul. Chaloupky,  ZP 246 m², zahrada 350 m², k modernizaci", (246, 350)),  # CENTURY21
        ("Prodej rodinného domu, 165m2, k.ú. Dyjákovice u Znojma", (165, None)),         # NEMZNOJMO
        ("Prodej stavební parcely 1362 m2 – Luka nad Jihlavou", (None, 1362)),           # HVREALITY
        ("Prodej podílu 1/2 pole 54 498 m², Černín a Ratišovice", (54498, None)),        # LEXAMO
        ("Prodej stavebního pozemku 1 133 m² - Štítary", (None, 1133)),                 # PRODEJMETO
        ("Prodej rodinného domu 5+kk, 182 m2 - Hevlín", (182, None)),                    # PRODEJMETO
        ("Prodej bytu 3+1 75 m²", (75, None)),
        ("Prodej rodinného domu 129 m², pozemek 1 238 m²", (129, 1238)),
        ("Prostorný rodinný dům 4+kk, garáž, zahrada, bazén, obec Morašice", (None, None)),
        ("Prodej chaty 5 m²", (None, None)),                                            # pod 10 m² = šum
    ])
    def test_real_titles(self, title, expected):
        assert parse_title_areas(title) == expected


class TestParseDescriptionLand:
    def test_land_with_adjective(self):
        assert parse_description_land("Dům stojí na pozemku o celkové výměře 820 m2.") == 820

    def test_no_land(self):
        assert parse_description_land("Kuchyň 12 m², obývák 25 m².") is None


class TestTitleOffersLand:
    @pytest.mark.parametrize("title, expected", [
        ("Prodej stavební parcely 1362 m2 – Luka nad Jihlavou", True),
        ("Prodej posledních 4 pozemků z celkových 20ti, k.ú. Havraníky u Znojma", True),
        ("Prodej zahrady 3 930 m², Dyjákovice", True),
        ("Prodej zemědělská půda, 3 575 m² - Běhařovice", True),
        ("Zahrada, Znojmo-Klínek", True),
        ("Prodej rodinného domu se zahradou", False),
        ("Prodej zahrady s chatkou, 1 034 m²", False),     # chatka = stavba, typ neměnit
        ("Pronájem komerčního prostoru 27m2, Family City", False),
        ("Prodej půdních prostor 160 m2 – Jihlava", False),
    ])
    def test_real_titles(self, title, expected):
        assert title_offers_land(title) is expected


class TestEnrichAreas:
    def test_other_type_land_title_becomes_land_with_area(self):
        d = {"title": "Prodej stavební parcely 1362 m2 – Luka nad Jihlavou", "property_type": "Ostatní"}
        _enrich_listing_fields(d)
        assert (d["property_type"], d.get("area_built_up"), d["area_land"]) == ("Pozemek", None, 1362)

    def test_land_single_area_goes_to_land(self):
        # LEXAMO: "Celková plocha" pozemku přišla jako area_built_up
        d = {"title": "Prodej zahrady 3 930 m², Dyjákovice", "property_type": "Pozemek", "area_built_up": 3930}
        _enrich_listing_fields(d)
        assert (d["area_built_up"], d["area_land"]) == (None, 3930)

    def test_house_land_from_description(self):
        # NEMZNOJMO Práče – titulek bez čísel, výměra jen v popisu
        d = {"title": "Vícegenerační rodinný dům 4+1 a 1+1 se zahradou, k.ú. Práče u Znojma",
             "description": "Dům nabízí pozemky o celkové výměře 820 m². Kuchyň 12 m².",
             "property_type": "Dům"}
        _enrich_listing_fields(d)
        assert (d.get("area_built_up"), d["area_land"]) == (None, 820)

    def test_scraper_values_are_kept(self):
        d = {"title": "Prodej rodinného domu 5+kk, 182 m2 - Hevlín", "property_type": "Dům",
             "area_built_up": 180, "area_land": 500}
        _enrich_listing_fields(d)
        assert (d["area_built_up"], d["area_land"]) == (180, 500)

    def test_missing_land_filled_from_title(self):
        d = {"title": "RD 2+kk, ZP 246 m², zahrada 350 m²", "property_type": "Dům", "area_built_up": 246}
        _enrich_listing_fields(d)
        assert d["area_land"] == 350
