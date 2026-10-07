"""Tests for _enrich_listing_fields in database.py"""
import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(__file__))))

from scraper.core.database import _enrich_listing_fields


def test_disposition_from_title():
    d = {"title": "Prodej rodinného domu 4+kk, 153m2 - Znojmo", "description": ""}
    _enrich_listing_fields(d)
    assert d["disposition"] == "4+KK", f"got {d.get('disposition')}"
    assert d["rooms"] == 4


def test_condition_and_construction_from_desc():
    d = {"title": "Prodej domu", "description": "Dům je po kompletní rekonstrukci, cihlová stavba."}
    _enrich_listing_fields(d)
    assert d["condition"] == "Po rekonstrukci", f"got {d.get('condition')}"
    assert d["construction_type"] == "Cihla", f"got {d.get('construction_type')}"


def test_novostavba():
    d = {"title": "Prodej novostavby 3+kk Brno", "description": ""}
    _enrich_listing_fields(d)
    assert d["condition"] == "Novostavba", f"got {d.get('condition')}"
    assert d["disposition"] == "3+KK", f"got {d.get('disposition')}"


def test_existing_fields_not_overwritten():
    d = {
        "title": "Byt 3+1",
        "description": "Po rekonstrukci",
        "disposition": "5+1",
        "condition": "Výborný",
    }
    _enrich_listing_fields(d)
    assert d["disposition"] == "5+1", "disposition should not be overwritten"
    assert d["condition"] == "Výborný", "condition should not be overwritten"


def test_pred_rekonstrukci():
    d = {"title": "Dům k rekonstrukci", "description": "Nemovitost vyžaduje rekonstrukci, dřevostavba."}
    _enrich_listing_fields(d)
    assert d["condition"] == "Před rekonstrukcí", f"got {d.get('condition')}"
    assert d["construction_type"] == "Dřevo", f"got {d.get('construction_type')}"


def test_panel():
    d = {"title": "Prodej bytu 3+1", "description": "Panelový dům v klidné lokalitě."}
    _enrich_listing_fields(d)
    assert d["construction_type"] == "Panel", f"got {d.get('construction_type')}"


def test_no_disposition_pozemek():
    d = {"title": "Prodej stavebního pozemku 467 m²", "description": "Pěkný pozemek."}
    _enrich_listing_fields(d)
    assert d.get("disposition") is None
    assert d.get("rooms") is None


if __name__ == "__main__":
    tests = [fn for name, fn in globals().items() if name.startswith("test_")]
    passed = 0
    for fn in tests:
        try:
            fn()
            passed += 1
            print(f"  PASS  {fn.__name__}")
        except AssertionError as e:
            print(f"  FAIL  {fn.__name__}: {e}")
    print(f"\n{passed}/{len(tests)} passed")


def test_negovane_klicove_slovo_stavu_se_ignoruje():
    d = {"title": "Rodinný dům 4+1", "description": "Dům z roku 1986, není novostavba, ale je udržovaný."}
    _enrich_listing_fields(d)
    assert d.get("condition") != "Novostavba"


def test_nenegovane_klicove_slovo_stavu_plati():
    d = {"title": "Rodinný dům 4+kk", "description": "Novostavba z roku 2025, kolaudace proběhla."}
    _enrich_listing_fields(d)
    assert d.get("condition") == "Novostavba"


def test_negace_plati_jen_pro_dany_vyskyt():
    d = {"title": "Dům", "description": "Není novostavba. Ale sousední objekt je novostavba po kolaudaci."}
    _enrich_listing_fields(d)
    assert d.get("condition") == "Novostavba"


def test_drevena_okna_nejsou_drevostavba():
    # Dyjákovice (6. 10. 2026): „okna jsou kombinací starších dřevěných a plastových" → uložilo se „Dřevo"
    for desc in (
        "Dům má sedlovou střechu, okna jsou kombinací starších dřevěných a plastových.",
        "V pokojích jsou dřevěné podlahy, původní dřevěné trámy a dřevěné schodiště.",
        "Na zahradě stojí pergola, podlahy jsou ze dřeva.",
    ):
        d = {"title": "Prodej rodinného domu 116 m², pozemek 2006 m²", "description": desc}
        _enrich_listing_fields(d)
        assert d.get("construction_type") is None, f"{desc!r} → {d.get('construction_type')}"


def test_drevena_stavba_je_drevo():
    for desc in (
        "Jedná se o dřevěnou stavbu s tradičním vzhledem.",
        "Nabízíme roubenku po rekonstrukci.",
        "Dům je postaven jako rámová dřevěná konstrukce.",
        "Zděný sklep, nad ním dřevěná chata.",
        "Dům je celý ze dřeva.",
    ):
        d = {"title": "Prodej chaty", "description": desc}
        _enrich_listing_fields(d)
        assert d.get("construction_type") == "Dřevo", f"{desc!r} → {d.get('construction_type')}"


def test_stav_se_sjednoti_napric_zdroji():
    # RealityMIX a Realcity (audit 7. 10. 2026): „velmi dobrý", „dobrý stav", „ve výstavbě (hrubá stavba)"
    from core.database import normalize_condition
    assert normalize_condition("velmi dobrý") == "Velmi dobrý"
    assert normalize_condition(" velmi dobrý stav ") == "Velmi dobrý"
    assert normalize_condition("dobrý stav") == "Dobrý"
    assert normalize_condition("ve výstavbě (hrubá stavba)") == "Ve výstavbě"
    assert normalize_condition("určený k demolici") == "K demolici"
    assert normalize_condition("Nutná rekonstrukce") == "Nutná rekonstrukce"
    assert normalize_condition(None) is None
    d = {"title": "Prodej domu", "description": "Dům v Kuřimi.", "condition": "novostavba"}
    _enrich_listing_fields(d)
    assert d["condition"] == "Novostavba"


def test_gps_mimo_kraj_se_zahodi_a_prohozena_otoci():
    # RealityMIX (7. 10. 2026): Kadov u Blatné místo Kadova na Znojemsku, Božice s prohozenou šířkou a délkou
    from core.database import sanitize_gps
    d = {"source_code": "REALITYMIX", "latitude": 49.402772, "longitude": 13.774866}
    sanitize_gps(d)
    assert d["latitude"] is None and d["longitude"] is None
    d = {"latitude": 16.288222, "longitude": 48.829566}
    sanitize_gps(d)
    assert (d["latitude"], d["longitude"]) == (48.829566, 16.288222)
    d = {"latitude": "48.9048", "longitude": "15.5973"}      # Vratěnín – nejzápadnější obec okresu
    sanitize_gps(d)
    assert (d["latitude"], d["longitude"]) == (48.9048, 15.5973)
    d = {"latitude": None, "longitude": 16.0}
    sanitize_gps(d)
    assert d["latitude"] is None
