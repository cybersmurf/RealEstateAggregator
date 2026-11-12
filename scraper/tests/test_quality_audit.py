"""Testy živé kontroly v denním auditu kvality (scripts/quality_audit.py)."""
import importlib.util
from pathlib import Path

import httpx

_spec = importlib.util.spec_from_file_location(
    "quality_audit", Path(__file__).resolve().parent.parent / "scripts" / "quality_audit.py")
quality_audit = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(quality_audit)

URL = "https://www.premiareality.cz/domy/prodej-rodinneho-domu-znojmo-okres-znojmo-dum-4-1-na-prodej-3052.html"
RESERVED_PAGE = """<table class="table"><tr><td colspan="2" class="rezervace">REZERVACE</td></tr>
<tr><td>Nemovitost</td><td><strong>Dům</strong></td></tr></table>"""
PRICED_PAGE = """<table class="table"><tr><td>Cena</td><td><strong>5 990 000 Kč</strong></td></tr></table>"""


def _client(body: str) -> httpx.AsyncClient:
    return httpx.AsyncClient(transport=httpx.MockTransport(lambda request: httpx.Response(200, text=body)))


async def test_reserved_listing_without_price_on_page_is_not_flagged() -> None:
    item = {"url": URL, "external_id": "3052", "price": 5990000, "price_note": "Rezervace", "photos": []}
    async with _client(RESERVED_PAGE) as client:
        res = await quality_audit._live_one(client, item)
    assert res["price_ok"] is None
    assert res["dead"] is False


async def test_missing_price_without_reservation_is_flagged() -> None:
    item = {"url": URL, "external_id": "3052", "price": 5990000, "price_note": None, "photos": []}
    async with _client(RESERVED_PAGE) as client:
        res = await quality_audit._live_one(client, item)
    assert res["price_ok"] is False


async def test_reserved_listing_with_price_on_page_still_compares_price() -> None:
    item = {"url": URL, "external_id": "3052", "price": 5990000, "price_note": "Rezervace", "photos": []}
    async with _client(PRICED_PAGE) as client:
        res = await quality_audit._live_one(client, item)
    assert res["price_ok"] is True


UNIT_PAGE = """<div class="advert-detail-heading__price"><span>3 700 Kč</span> <span>(za m<sup>2</sup>)</span></div>"""


async def test_unit_price_listing_compares_price_per_m2() -> None:
    """Cena za m² (10. 11. 2026): uložená je celková 783 × 3 700, web ukazuje jen 3 700 Kč/m²."""
    item = {"url": URL, "external_id": "7868449", "price": 2897100, "price_note": "3 700 Kč/m²", "photos": []}
    async with _client(UNIT_PAGE) as client:
        res = await quality_audit._live_one(client, item)
    assert res["price_ok"] is True


async def test_unit_price_mismatch_is_flagged() -> None:
    item = {"url": URL, "external_id": "7868449", "price": 3132000, "price_note": "4 000 Kč/m²", "photos": []}
    async with _client(UNIT_PAGE) as client:
        res = await quality_audit._live_one(client, item)
    assert res["price_ok"] is False


def test_missed_nightly_run_is_red_at_doctor_time() -> None:
    from datetime import datetime, timezone
    # 12. 11. 2026: restart serveru zrušil běh ve 3:00, doktor se ptal ve 4:30 (3:30 UTC)
    last = datetime(2026, 11, 11, 2, 0, tzinfo=timezone.utc)
    level, message = quality_audit.nightly_finding(last, datetime(2026, 11, 12, 3, 30, tzinfo=timezone.utc))
    assert level == "red"
    assert "11. 11. 02:00" in message


def test_nightly_run_from_tonight_is_fine() -> None:
    from datetime import datetime, timezone
    last = datetime(2026, 11, 12, 2, 0, tzinfo=timezone.utc)
    assert quality_audit.nightly_finding(last, datetime(2026, 11, 12, 3, 30, tzinfo=timezone.utc))[0] is None
    # audit spuštěný během dalšího nočního běhu ještě nehlásí
    assert quality_audit.nightly_finding(last, datetime(2026, 11, 13, 2, 40, tzinfo=timezone.utc))[0] is None
    assert quality_audit.nightly_finding(None, datetime(2026, 11, 12, 3, 30, tzinfo=timezone.utc))[0] == "red"
