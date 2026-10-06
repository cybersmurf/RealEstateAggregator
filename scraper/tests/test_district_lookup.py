"""Okres z názvu obce – záloha pro inzeráty bez okresu i bez GPS (Prodejme.to, iDNES)."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.district_lookup import district_from_place_names, normalize_place

KNOWN = {
    "brno": "Brno-město",
    "suchohrdly": "Znojmo",
    "pohorelice": "Brno-venkov",
    "jirice u miroslavi": "Znojmo",
    "miroslav": "Znojmo",
    "miroslavske kninice": "Znojmo",
    "pasohlavky": "Brno-venkov",
}


class TestOkresZNazvuObce:

    def test_normalizace(self):
        assert normalize_place("  Jiřice u  Miroslavi ") == "jirice u miroslavi"

    @pytest.mark.parametrize("location_text, municipality, expected", [
        ("Suchohrdly, Jihomoravský kraj", "Suchohrdly", "Znojmo"),     # Prodejme.to
        ("Brno, Jihomoravský kraj", None, "Brno-město"),
        ("Havránkova, Brno", None, "Brno-město"),                      # ulice, obec
        ("Znojemská, Pohořelice", None, "Brno-venkov"),                # ulice není obec
        ("Jirice U Miroslavi", None, "Znojmo"),                        # iDNES bez diakritiky
        ("Pohorelice Znojemska", None, "Brno-venkov"),                 # obec + ulice bez čárky
        ("Miroslav Merunkova", None, "Znojmo"),
        ("Miroslavske Kninice", None, "Znojmo"),                       # delší název má přednost před "Miroslav"
        ("Pasohlávky\n – \n část obce Mušov", None, "Brno-venkov"),    # RE/MAX víceřádková adresa
    ])
    def test_najde_okres(self, location_text, municipality, expected):
        assert district_from_place_names(location_text, municipality, KNOWN) == expected

    @pytest.mark.parametrize("location_text", [
        "Praha 18 Miroslava Hajna",      # ulice Miroslava Hájka v Praze – iDNES ji vrací na dotaz "Miroslav"
        "Ostrava Miroslava Bajera",
        "Horni Jeleni Miroslavska",
        "Hrušky 41, Hrušky, Jihomoravský kraj",   # Hrušky jsou v okrese Břeclav i Vyškov → ve slovníku nejsou
        "",
        None,
    ])
    def test_neznama_nebo_nejednoznacna_obec_zustane_bez_okresu(self, location_text):
        assert district_from_place_names(location_text, None, KNOWN) is None
