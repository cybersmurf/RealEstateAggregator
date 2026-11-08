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
