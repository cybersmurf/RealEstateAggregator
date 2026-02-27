"""
Unit testy pro FilterManager (core/filters.py).
Testují geo filtrování, quality filtry a cenové limity – bez DB nebo HTTP.
"""
import sys
from pathlib import Path
from typing import Any, Dict, Optional

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.filters import FilterManager


# ---------------------------------------------------------------------------
# Fixtures a helpers
# ---------------------------------------------------------------------------

def make_fm(overrides: Optional[Dict[str, Any]] = None) -> FilterManager:
    """Vytvoří FilterManager s výchozí in-memory konfigurací (bez settings.yaml)."""
    fm = FilterManager.__new__(FilterManager)
    fm.config = {
        "search_filters": {
            "target_districts": ["Znojmo"],
            "houses": {"enabled": True, "max_price": 8_500_000},
            "land": {"enabled": True, "max_price": 2_000_000},
        },
        "quality_filters": {
            "require_photos": True,
            "require_price": True,
            "require_location": True,
        },
    }
    if overrides:
        for section, values in overrides.items():
            fm.config.setdefault(section, {}).update(values)
    fm.search_filters = fm.config["search_filters"]
    fm.quality_filters = fm.config["quality_filters"]
    return fm


def valid_listing(**kwargs) -> Dict[str, Any]:
    """Vytvoří validní listing vyhovující výchozí konfiguraci."""
    base: Dict[str, Any] = {
        "title": "Rodinný dům 5+1",
        "price": 4_000_000,
        "location_text": "Znojmo, Jihomoravský kraj",
        "property_type": "House",
        "offer_type": "Sale",
        "photos": ["https://example.cz/foto1.jpg"],
        "description": "Krásný rodinný dům v klidném prostředí.",
    }
    base.update(kwargs)
    return base


# ---------------------------------------------------------------------------
# Quality filtry
# ---------------------------------------------------------------------------

class TestQualityFilterPhotos:
    def test_bez_fotek_vyloucen(self):
        fm = make_fm()
        listing = valid_listing(photos=[])
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert reason is not None

    def test_s_fotkou_projde(self):
        fm = make_fm()
        listing = valid_listing(photos=["https://example.cz/foto1.jpg"])
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_require_photos_false_nevylucuje_bez_fotek(self):
        fm = make_fm({"quality_filters": {"require_photos": False, "require_price": False, "require_location": False}})
        listing = valid_listing(photos=[])
        ok, _ = fm.should_include_listing(listing)
        assert ok is True


class TestQualityFilterPrice:
    def test_bez_ceny_vyloucen(self):
        fm = make_fm()
        listing = valid_listing(price=None)
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert "price" in (reason or "").lower()

    def test_s_cenou_projde(self):
        fm = make_fm()
        listing = valid_listing(price=3_000_000)
        ok, _ = fm.should_include_listing(listing)
        assert ok is True


class TestQualityFilterLocation:
    def test_bez_lokace_vyloucen(self):
        fm = make_fm()
        listing = valid_listing(location_text="")
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert reason is not None

    def test_s_lokaci_projde(self):
        fm = make_fm()
        listing = valid_listing(location_text="Znojmo-město")
        ok, _ = fm.should_include_listing(listing)
        assert ok is True


# ---------------------------------------------------------------------------
# Geo filtr (target_districts)
# ---------------------------------------------------------------------------

class TestGeoFilter:
    def test_znojmo_projde(self):
        fm = make_fm()
        listing = valid_listing(location_text="Znojmo, Jihomoravský kraj")
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_znojmo_lowercase_projde(self):
        fm = make_fm()
        listing = valid_listing(location_text="znojmo-střed")
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_brno_vylouceno(self):
        fm = make_fm()
        listing = valid_listing(location_text="Brno, Jihomoravský kraj")
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert reason is not None

    def test_prazdna_lokace_vyloucena_geo(self):
        """Quality filtr zachytí prázdnou lokaci dříve, než geo filtr."""
        fm = make_fm()
        listing = valid_listing(location_text="")
        ok, reason = fm.should_include_listing(listing)
        assert ok is False

    def test_bez_target_districts_vsechno_projde(self):
        fm = make_fm({"search_filters": {"target_districts": []}})
        listing = valid_listing(location_text="Praha 1")
        ok, _ = fm.should_include_listing(listing)
        assert ok is True


# ---------------------------------------------------------------------------
# Cenový filtr – Houses
# ---------------------------------------------------------------------------

class TestPriceFilterHouses:
    def test_dum_pod_limitem_projde(self):
        fm = make_fm()
        listing = valid_listing(property_type="House", price=5_000_000)
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_dum_na_limitu_projde(self):
        fm = make_fm()
        listing = valid_listing(property_type="House", price=8_500_000)
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_dum_nad_limitem_vyloucen(self):
        fm = make_fm()
        listing = valid_listing(property_type="House", price=9_000_000)
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert reason is not None

    def test_houses_disabled_vylucuje(self):
        fm = make_fm({"search_filters": {"houses": {"enabled": False}}})
        listing = valid_listing(property_type="House", price=3_000_000)
        ok, reason = fm.should_include_listing(listing)
        assert ok is False


# ---------------------------------------------------------------------------
# Cenový filtr – Land
# ---------------------------------------------------------------------------

class TestPriceFilterLand:
    def test_pozemek_pod_limitem_projde(self):
        fm = make_fm()
        listing = valid_listing(property_type="Land", price=1_500_000)
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_pozemek_nad_limitem_vyloucen(self):
        fm = make_fm()
        listing = valid_listing(property_type="Land", price=3_000_000)
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert reason is not None


# ---------------------------------------------------------------------------
# Kombinované scénáře
# ---------------------------------------------------------------------------

class TestCombinedFilters:
    def test_vsechno_validni_projde(self):
        fm = make_fm()
        listing = valid_listing()
        ok, reason = fm.should_include_listing(listing)
        assert ok is True
        assert reason is None

    def test_apartment_nema_cenovy_limit(self):
        """Pro Apartment není definovaný max_price – drahý byt projde."""
        fm = make_fm()
        listing = valid_listing(property_type="Apartment", price=20_000_000)
        ok, _ = fm.should_include_listing(listing)
        assert ok is True

    def test_vice_duvodu_vylouceni_vraci_prvni(self):
        """Pokud selže quality filtr, nepokračujeme na geo filtr – vrátí první chybu."""
        fm = make_fm()
        listing = valid_listing(photos=[], location_text="Brno")
        ok, reason = fm.should_include_listing(listing)
        assert ok is False
        assert reason is not None


# ---------------------------------------------------------------------------
# FilterManager – default konfigurace (bez config souboru)
# ---------------------------------------------------------------------------

class TestFilterManagerDefaultConfig:
    def test_default_config_obsahuje_target_districts(self):
        fm = FilterManager.__new__(FilterManager)
        cfg = fm._get_default_config()
        assert "Znojmo" in cfg["search_filters"]["target_districts"]

    def test_default_config_quality_filters(self):
        fm = FilterManager.__new__(FilterManager)
        cfg = fm._get_default_config()
        assert cfg["quality_filters"]["require_photos"] is True
        assert cfg["quality_filters"]["require_price"] is True
        assert cfg["quality_filters"]["require_location"] is True

    def test_default_config_max_price_house(self):
        fm = FilterManager.__new__(FilterManager)
        cfg = fm._get_default_config()
        assert cfg["search_filters"]["houses"]["max_price"] == 8_500_000

    def test_default_config_max_price_land(self):
        fm = FilterManager.__new__(FilterManager)
        cfg = fm._get_default_config()
        assert cfg["search_filters"]["land"]["max_price"] == 2_000_000
