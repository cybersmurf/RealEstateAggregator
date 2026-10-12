"""HV Reality – neúplný REST výpis nesmí vést k deaktivaci živých inzerátů.

12. 10. 2026 v 01:00 selhala 2. stránka REST výpisu prodeje (timeout s prázdnou hláškou);
scraper vrátil 100 nabídek místo 253 a plný běh deaktivoval 40 inzerátů, které na webu visí.
"""
import asyncio
import re
import sys
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.hvreality_scraper import HvRealityScraper

SALE = "https://hvreality.cz/prodej-nemovitosti/"


def _item(n: int) -> dict:
    return {"id": n, "link": f"{SALE}prodej-domu-znojmo-okres-znojmo-prodej-domu-{n}/",
            "title": {"rendered": f"Dům {n}"}, "modified": "2026-10-12T00:00:00",
            "acf": {"advert_state": "active", "advert_price": 1000000, "advert_county": "Znojmo"}}


class _Response:
    def __init__(self, items: list, total_pages: int):
        import json
        self.text = json.dumps(items)
        self.headers = {"X-WP-TotalPages": str(total_pages)}

    def raise_for_status(self) -> None:
        pass


class _Client:
    """Prodej: 2 stránky, druhá padá na timeout; pronájem: 1 stránka."""

    def __init__(self, second_page_fails: int):
        self.calls: list = []
        self.second_page_fails = second_page_fails

    async def get(self, url: str):
        self.calls.append(url)
        if "pronajem-nemovitosti" in url:
            return _Response([_item(300)], 1)
        page = int(re.search(r"&page=(\d+)", url).group(1))
        if page == 1:
            return _Response([_item(1), _item(2)], 2)
        if self.second_page_fails > 0:
            self.second_page_fails -= 1
            raise httpx.ReadTimeout("")
        return _Response([_item(3)], 2)


def _run(client: _Client, full_rescan: bool = True):
    scraper = HvRealityScraper()
    scraper.REST_PAGE_ATTEMPTS = 3
    scraper._http_client = client
    orig_sleep = asyncio.sleep

    async def no_sleep(_):
        return None

    asyncio.sleep = no_sleep
    try:
        items = asyncio.run(scraper._enumerate_rest(full_rescan))
    finally:
        asyncio.sleep = orig_sleep
    return scraper, items


def test_stranka_se_opakuje_a_po_uspechu_je_vypis_uplny():
    client = _Client(second_page_fails=1)
    scraper, items = _run(client)
    assert scraper.lists_complete is True
    assert {it["url"] for it in items} == {_item(n)["link"] for n in (1, 2, 3, 300)}
    assert sum("&page=2" in u and "prodej" in u for u in client.calls) == 2


def test_trvale_selhani_vrati_castecny_vypis_a_oznaci_ho_neuplnym():
    client = _Client(second_page_fails=10)
    scraper, items = _run(client)
    assert scraper.lists_complete is False
    assert {it["url"] for it in items} == {_item(n)["link"] for n in (1, 2, 300)}
    assert sum("&page=2" in u and "prodej" in u for u in client.calls) == 3


def test_inkrementalni_beh_bere_jen_prvni_stranku():
    client = _Client(second_page_fails=10)
    scraper, items = _run(client, full_rescan=False)
    assert scraper.lists_complete is True
    assert len(items) == 3
