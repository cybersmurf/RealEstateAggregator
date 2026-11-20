"""Znojmo Reality – výpis, který se nestáhl, nesmí vést k deaktivaci živých nabídek.

19. 11. 2026 vypršel 30s limit u výpisu /ostatni (web odpovídá 20–30 s na stránku); scraper
vrátil 7 nabídek z 10 a plný běh deaktivoval 3, které na webu visely dál.
"""
import asyncio
from pathlib import Path

import httpx

from core.scrapers import znojmoreality_scraper as module
from core.scrapers.znojmoreality_scraper import BASE_URL, ZnojmoRealityScraper

FIX = Path(__file__).parent / "fixtures" / "znojmoreality"


class _Db:
    def __init__(self) -> None:
        self.marked: list = []

    async def mark_active_seen(self, source_code: str) -> int:
        self.marked.append(source_code)
        return 10


def _run(monkeypatch, failing: str = ""):
    scraper = ZnojmoRealityScraper()
    db = _Db()
    saved: list = []
    list_html = (FIX / "list_domy.html").read_text(encoding="utf-8")

    async def fetch(url: str) -> str:
        if failing and url.endswith(failing):
            raise httpx.ReadTimeout("")
        return list_html if url == f"{BASE_URL}/domy" else "<html></html>"

    async def save(listing: dict) -> None:
        saved.append(listing["external_id"])

    async def no_sleep(_) -> None:
        return None

    monkeypatch.setattr(scraper, "_fetch", fetch)
    monkeypatch.setattr(scraper, "_save_listing", save)
    monkeypatch.setattr(scraper, "_parse_detail", lambda html, item: {"external_id": item["external_id"]})
    monkeypatch.setattr(module, "get_db_manager", lambda: db)
    monkeypatch.setattr(module.asyncio, "sleep", no_sleep)
    count = asyncio.run(scraper.run(full_rescan=True))
    return scraper, db, saved, count


def test_uplny_beh_nic_nehlasi(monkeypatch):
    scraper, db, saved, count = _run(monkeypatch)
    assert scraper.lists_complete is True
    assert db.marked == []
    assert count == len(saved) > 0


def test_nestazeny_vypis_oznaci_beh_neuplnym(monkeypatch):
    scraper, db, saved, count = _run(monkeypatch, failing="/ostatni")
    assert scraper.lists_complete is False
    assert db.marked == ["ZNOJMOREALITY"]
    assert count == len(saved) > 0


def test_nestazeny_detail_oznaci_beh_neuplnym(monkeypatch):
    scraper, db, saved, count = _run(monkeypatch, failing="-726")
    assert scraper.lists_complete is False
    assert db.marked == ["ZNOJMOREALITY"]
    assert "726" not in saved


def test_vycerpany_cas_oznaci_beh_neuplnym(monkeypatch):
    monkeypatch.setattr(module, "TIME_BUDGET_SECONDS", -1)
    scraper, db, saved, count = _run(monkeypatch)
    assert scraper.lists_complete is False
    assert db.marked == ["ZNOJMOREALITY"]
    assert saved == []
