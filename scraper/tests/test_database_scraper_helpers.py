"""DatabaseManager – pomocné dotazy dvoufázových scraperů a důvod deaktivace v logu (bez DB)."""
import asyncio
import logging
import sys
from pathlib import Path
from typing import Any, List, Tuple

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.database import DatabaseManager


class _FakeConn:
    def __init__(self, rows: list = (), status: str = "UPDATE 1") -> None:
        self.rows, self.status = list(rows), status
        self.calls: List[Tuple[str, tuple]] = []

    async def fetch(self, query: str, *args: Any) -> list:
        self.calls.append((query, args))
        return self.rows

    async def execute(self, query: str, *args: Any) -> str:
        self.calls.append((query, args))
        return self.status


class _FakeAcquire:
    def __init__(self, conn: _FakeConn) -> None:
        self.conn = conn

    async def __aenter__(self) -> _FakeConn:
        return self.conn

    async def __aexit__(self, *exc: Any) -> None:
        return None


def _manager(conn: _FakeConn) -> DatabaseManager:
    manager = DatabaseManager.__new__(DatabaseManager)
    manager.acquire = lambda: _FakeAcquire(conn)
    return manager


class TestDeaktivaceSDuvodem:

    def test_bez_duvodu_puvodni_zprava(self, caplog):
        with caplog.at_level(logging.INFO, logger="core.database"):
            assert asyncio.run(_manager(_FakeConn()).deactivate_listing("REALMIX", "1")) is True
        assert "Deactivated sold/reserved listing: source=REALMIX external_id=1" in caplog.text

    def test_duvod_v_logu(self, caplog):
        with caplog.at_level(logging.INFO, logger="core.database"):
            asyncio.run(_manager(_FakeConn()).deactivate_listing(
                "REALINGO", "2", reason="out of search filters (list)"))
        assert "Deactivated listing (out of search filters (list)): source=REALINGO external_id=2" in caplog.text
        assert "sold/reserved" not in caplog.text

    def test_uz_stazeny_nic_nehlasi(self, caplog):
        with caplog.at_level(logging.INFO, logger="core.database"):
            assert asyncio.run(_manager(_FakeConn(status="UPDATE 0")).deactivate_listing("IDNES", "3", reason="x")) is False
        assert "Deactivated" not in caplog.text


class TestPomocneDotazy:

    def test_ulozene_fotky_podle_inzeratu(self):
        conn = _FakeConn([{"external_id": "1", "urls": ["a", "b", "a"]}, {"external_id": "2", "urls": None}])
        assert asyncio.run(_manager(conn).get_stored_photo_urls("REALINGO")) == {"1": {"a", "b"}, "2": set()}
        query, args = conn.calls[0]
        assert args == ("REALINGO",)
        assert "listing_photos" in query and "original_url" in query

    def test_zname_inzeraty_s_typem_a_aktivitou(self):
        conn = _FakeConn([
            {"external_id": "1", "price": 18_490_000, "property_type": "Commercial", "is_active": True},
            {"external_id": "2", "price": None, "property_type": "House", "is_active": False},
        ])
        assert asyncio.run(_manager(conn).get_known_listings("REALITYCECHY")) == {
            "1": {"price": 18_490_000.0, "property_type": "Commercial", "is_active": True},
            "2": {"price": None, "property_type": "House", "is_active": False},
        }

    def test_zapamatovane_s_duvodem(self):
        conn = _FakeConn([{"external_id": "1", "reason": "origin scraped directly"},
                          {"external_id": "2", "reason": None}])
        manager = _manager(conn)
        assert asyncio.run(manager.get_skip_reasons("REALINGO")) == {"1": "origin scraped directly", "2": ""}
        assert asyncio.run(manager.get_skipped_ids("REALINGO")) == {"1", "2"}
