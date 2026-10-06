"""Realingo – výběr inzerátů k detailu a údaje z výpisu (bez HTTP a DB)."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.realingo_scraper import DEFAULT_LISTS, RealingoScraper


def _list_html(offers: list, total: int) -> str:
    data = {"props": {"pageProps": {"store": {"offer": {"list": {"data": offers, "total": total}}}}}}
    return f'<html><script id="__NEXT_DATA__" type="application/json">{json.dumps(data)}</script></html>'


class TestRealingoVypis:

    def test_polozka_nese_id_adresu_a_cenu(self):
        html = _list_html([
            {"id": 24735201, "url": "/prodej/dum-rodinny-sentice/24735201", "price": {"total": 6700000}},
            {"id": 24735202, "url": "/prodej/dum-rodinny-kurim/24735202", "price": {"total": None}},
            {"id": 24735203, "url": None},
        ], total=434)
        items, total = RealingoScraper().parse_list_page(html)
        assert total == 434
        assert items == [
            {"id": "24735201", "url": "/prodej/dum-rodinny-sentice/24735201", "price": 6700000},
            {"id": "24735202", "url": "/prodej/dum-rodinny-kurim/24735202", "price": None},
        ]

    def test_brno_venkov_je_ve_vychozich_vypisech(self):
        """6. 10. 2026: portál měl v okrese Brno-venkov 434 domů a nebrali jsme z nich nic."""
        paths = [path for path, _, _ in DEFAULT_LISTS]
        assert "/prodej_domy/Okres_Brno-venkov/" in paths
        assert "/prodej_domy/Okres_Znojmo/" in paths


class TestRealingoVyberProDetail:
    ITEMS = [
        {"id": "1", "url": "/a/1", "price": 5_000_000},   # známý, cena stejná
        {"id": "2", "url": "/a/2", "price": 4_500_000},   # známý, zlevnil
        {"id": "3", "url": "/a/3", "price": 3_000_000},   # nový
        {"id": "4", "url": "/a/4", "price": 2_000_000},   # zapamatovaný – bereme ho z iDNES
        {"id": "5", "url": "/a/5", "price": None},        # známý, výpis bez ceny
    ]
    KNOWN = {"1": 5_000_000.0, "2": 4_900_000.0, "5": 1_000_000.0}

    def test_zmena_ceny_napred_pak_nove_zapamatovane_ne(self):
        todo = RealingoScraper.select_for_detail(self.ITEMS, self.KNOWN, {"4"})
        assert [i["id"] for i in todo] == ["2", "3"]

    def test_bez_pameti_se_stahuje_i_drive_preskoceny(self):
        todo = RealingoScraper.select_for_detail(self.ITEMS, self.KNOWN, set())
        assert [i["id"] for i in todo] == ["2", "3", "4"]
