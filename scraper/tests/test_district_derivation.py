"""Okres před filtrem lokality – slovník obcí a pořadí zdrojů (GPS → název obce)."""
import asyncio
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.database import DatabaseManager
from core.district_lookup import district_from_place_names
from core.district_municipalities import official_municipality_districts


class TestUredniSeznamObci:

    def setup_method(self):
        self.known = official_municipality_districts()

    @pytest.mark.parametrize("location, expected", [
        ("ulice Dlouhá, Hrabětice", "Znojmo"),                    # RE/MAX
        ("Strachotice – část obce Micmanice", "Znojmo"),          # RE/MAX
        ("Jevišovice", "Znojmo"),                                 # Premia Reality
        ("Rosice", "Brno-venkov"),                                # Century 21 – dřív „okres Znojmo"
        ("Hrušovany nad Jevišovkou", "Znojmo"),
        ("Kounicova, Brno", "Brno-město"),
    ])
    def test_obec_bez_okresu(self, location, expected):
        assert district_from_place_names(location, None, self.known) == expected

    def test_obce_stejneho_jmena_v_obou_okresech_ve_slovniku_nejsou(self):
        from core.district_municipalities import DISTRICT_MUNICIPALITY_SLUGS as slugs
        both = {s.replace("-", " ") for s in slugs["Znojmo"] & slugs["Brno-venkov"]}
        assert both, "seznamy mají obsahovat aspoň jednu obec společného jména"
        assert not both & set(self.known)

    def test_cizi_obec_zustane_bez_okresu(self):
        assert district_from_place_names("Wrocław, Polsko", None, self.known) is None


class _FakeConn:
    def __init__(self, polygon_district: Optional[str]):
        self.polygon_district = polygon_district
        self.queries: List[str] = []

    async def fetchval(self, query: str, *args: Any) -> Any:
        self.queries.append(query)
        if "to_regclass" in query:
            return True
        return self.polygon_district

    async def fetch(self, query: str, *args: Any) -> list:
        self.queries.append(query)
        return []


class _FakeAcquire:
    def __init__(self, conn: _FakeConn):
        self.conn = conn

    async def __aenter__(self) -> _FakeConn:
        return self.conn

    async def __aexit__(self, *exc: Any) -> None:
        return None


def _derive(data: Dict[str, Any], polygon_district: Optional[str] = None) -> _FakeConn:
    manager = DatabaseManager.__new__(DatabaseManager)
    conn = _FakeConn(polygon_district)
    manager.acquire = lambda: _FakeAcquire(conn)
    asyncio.run(manager._derive_district(data))
    return conn


class TestOdvozeniOkresu:

    def test_okres_od_scraperu_se_neprepisuje(self):
        data = {"source_code": "REMAX", "district": "Brno-venkov", "location_text": "Hrabětice"}
        conn = _derive(data, polygon_district="Znojmo")
        assert data["district"] == "Brno-venkov"
        assert conn.queries == []

    def test_gps_ma_prednost_pred_nazvem(self):
        data = {"source_code": "NEMZNOJMO", "location_text": "Parcela 3070, Načeratice",
                "latitude": 48.83, "longitude": 16.10}
        _derive(data, polygon_district="Znojmo")
        assert data["district"] == "Znojmo"

    def test_mistni_zdroj_bez_gps_podle_nazvu_obce(self):
        data = {"source_code": "PREMIAREALITY", "location_text": "Tvořihráz"}
        _derive(data)
        assert data["district"] == "Znojmo"

    def test_celostatni_zdroj_bez_gps_okres_z_nazvu_nedostane(self):
        """„Nová Ves" odjinud by jinak dostala náš okres a prošla filtrem."""
        data = {"source_code": "LEXAMO", "location_text": "Tvořihráz"}
        conn = _derive(data)
        assert "district" not in data
        assert conn.queries == []

    def test_gps_mimo_polygony_a_cizi_obec(self):
        data = {"source_code": "DELUXREALITY", "location_text": "Wrocław, Polsko",
                "latitude": 51.1, "longitude": 17.03}
        _derive(data, polygon_district=None)
        assert "district" not in data
