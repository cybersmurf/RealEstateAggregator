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
        "condition": "Dobrý stav",
    }
    _enrich_listing_fields(d)
    assert d["disposition"] == "5+1", "disposition should not be overwritten"
    assert d["condition"] == "Dobrý stav", "condition should not be overwritten"


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
