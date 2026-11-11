"""Bazoš – výpis: okres a cena z položky, výběr inzerátů k detailu, adresy hledání."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.bazos_scraper import SEARCHES, BazosScraper, search_url

LIST_HTML = """
<div class="maincontent">
<div class="inzeraty inzeratyflex">
  <div class="inzeratynadpis"><a href="/inzerat/224210973/prodej-domu-pohorelice.php"><img src="x.jpg" class="obrazek"></a>
    <h2 class=nadpis><a href="/inzerat/224210973/prodej-domu-pohorelice.php">Prodej rodinného domu 149 m², Pohořelice</a></h2>
    <div class=popis>Rodinný dům v klidné ulici ...</div></div>
  <div class="inzeratycena"><b><span translate="no"> 12 649 000 Kč</span></b></div>
  <div class="inzeratylok">Brno venkov<br>691 23</div>
</div>
<div class="inzeraty inzeratyflex">
  <div class="inzeratynadpis"><h2 class=nadpis><a href="/inzerat/224450984/prodej-domu-satov.php">Prodej rodinného domu s garáží, Šatov</a></h2></div>
  <div class="inzeratycena"><b>Dohodou</b></div>
  <div class="inzeratylok">Znojmo<br>671 22</div>
</div>
<div class="inzeraty inzeratyflex">
  <div class="inzeratynadpis"><h2 class=nadpis><a href="/inzerat/224000001/dum-vyskov.php">Dům Vyškov</a></h2></div>
  <div class="inzeratycena"><b> 5 000 000 Kč</b></div>
  <div class="inzeratylok">Vyškov<br>683 01</div>
</div>
<div class="inzeraty inzeratyflex">
  <div class="inzeratynadpis"><h2 class=nadpis><a href="/inzerat/224000002/byt-brno.php">Byt Brno</a></h2></div>
  <div class="inzeratycena"><b> 6 000 000 Kč</b></div>
  <div class="inzeratylok">Brno<br>602 00</div>
</div>
</div>
"""


class TestBazosVypis:
    """6. 10. 2026: jediné hledání „Znojmo + 25 km do 8,5 mil." – chyběl celý okres Brno-venkov."""

    def setup_method(self):
        self.items, self.other_district, self.raw_count = BazosScraper.parse_list_page(LIST_HTML)

    def test_jen_nase_okresy(self):
        assert [(i["external_id"], i["district"]) for i in self.items] == [
            ("224210973", "Brno-venkov"), ("224450984", "Znojmo")]
        assert (self.other_district, self.raw_count) == (2, 4)

    def test_cena_adresa_a_titulek(self):
        house = self.items[0]
        assert house["price"] == 12_649_000
        assert house["detail_url"] == "https://reality.bazos.cz/inzerat/224210973/prodej-domu-pohorelice.php"
        assert house["title"] == "Prodej rodinného domu 149 m², Pohořelice"

    def test_cena_dohodou_je_bez_ceny(self):
        assert self.items[1]["price"] is None

    def test_detail_jen_pro_nove_a_zmenenou_cenu(self):
        known = {"224210973": 12_990_000.0}
        assert [i["external_id"] for i in BazosScraper.select_for_detail(self.items, known)] == ["224210973", "224450984"]
        assert BazosScraper.select_for_detail(self.items, {"224210973": 12_649_000.0, "224450984": 7_600_000.0}) == []


class TestBazosHledani:

    def test_adresy_stranek(self):
        assert search_url("prodam/dum/", "60200", 30, 1).startswith("https://reality.bazos.cz/prodam/dum/?hledat=")
        assert search_url("prodam/dum/", "60200", 30, 3) == (
            "https://reality.bazos.cz/prodam/dum/40/?hledat=&hlokalita=60200&humkreis=30&cenaod=&cenado=&order=")
        assert search_url("", "66902", 35, 2).startswith("https://reality.bazos.cz/20/?hledat=&hlokalita=66902&humkreis=35")

    def test_hledani_nema_cenovy_strop_a_pokryva_okoli_brna(self):
        assert all("cenado=&" in search_url(c, p, r, 2) for c, p, r in SEARCHES)
        assert any(p == "60200" and c == "prodam/dum/" for c, p, r in SEARCHES)


def _detail_html(crumb_path: str, crumb_label: str) -> str:
    """Detail s levým menu (odkazy na všechny rubriky) před drobečkovou navigací – jako na webu."""
    return f"""
<div class="menuleft">
  <a href="/prodam/byt/">Byty</a><a href="/prodam/dum/">Domy</a><a href="/prodam/pozemek/">Pozemky</a>
  <a href="/pronajmu/byt/">Byty</a>
</div>
<div class="drobky"><a href="https://www.bazos.cz/">Hlavní stránka</a> > <a href="https://reality.bazos.cz/">Reality</a>
 > <a href="https://reality.bazos.cz/{crumb_path.split('/')[0]}/">X</a>
 > <a href="https://reality.bazos.cz/{crumb_path}/">{crumb_label}</a> > <b>Inzerát č. 1</b></div>
<h1 class="nadpisdetail">Titulek</h1>
"""


class TestBazosTypZRubriky:
    """6. 11. 2026: zahrady a pozemky z rubriky „zahrada" byly „byt" (první odkaz menu), pronájmy prodej."""

    def setup_method(self):
        from bs4 import BeautifulSoup
        self.scraper = BazosScraper()
        self.soup = lambda html: BeautifulSoup(html, "html.parser")

    def test_zahrada_je_pozemek_ne_byt(self):
        soup = self.soup(_detail_html("prodam/zahrada", "Zahrady"))
        assert self.scraper._infer_types(soup, "Prodej pozemku - zahrady v Kuřimi", "x") == ("Prodej", "Pozemek")

    def test_pronajem_z_rubriky_pronajmu(self):
        soup = self.soup(_detail_html("pronajmu/byt", "Byty"))
        assert self.scraper._infer_types(soup, "Ivančice centrum byt 1+kk vybavený", "x") == ("Pronájem", "Byt")

    def test_dum_v_rubrice_dum(self):
        soup = self.soup(_detail_html("prodam/dum", "Domy"))
        assert self.scraper._infer_types(soup, "Rodinný dům 4+1 se zahradou v centru Hevlína", "x") == ("Prodej", "Dům")

    def test_menu_bez_drobku_neurcuje_typ(self):
        html = '<div class="menuleft"><a href="/prodam/byt/">Byty</a></div><h1>Stodola</h1>'
        offer, ptype = self.scraper._infer_types(self.soup(html), "Stodola u lesa", "x")
        assert (offer, ptype) == ("Prodej", "Ostatní")


class TestBazosBlokaceVypisu:
    """11. 11. 2026: blokace hledání trvala ~5½ min, čekání 30 + 60 + 120 s vypršelo dřív → hledání
    padlo, výpis byl neúplný a 24 stažených inzerátů zůstalo aktivních."""

    URL = "https://reality.bazos.cz/prodam/chata/?hledat=&hlokalita=69201&humkreis=20"

    @staticmethod
    def _status_error(url: str, status: int) -> "httpx.HTTPStatusError":
        import httpx
        request = httpx.Request("GET", url)
        return httpx.HTTPStatusError(str(status), request=request, response=httpx.Response(status, request=request))

    def _run(self, scraper: BazosScraper, responses: list, monkeypatch) -> tuple[list[str], list[float], object]:
        """Odpovědi (výjimka nebo HTML) po řadě na každý požadavek; vrací požadavky, čekání a výsledek."""
        import asyncio

        calls: list[str] = []
        sleeps: list[float] = []

        async def fake_get(url: str) -> str:
            calls.append(url)
            response = responses[len(calls) - 1]
            if isinstance(response, Exception):
                raise response
            return response

        async def fake_sleep(seconds: float) -> None:
            sleeps.append(seconds)
            if scraper._list_deadline is not None:   # čas běží: do limitu zbývá méně
                scraper._list_deadline -= seconds

        scraper._get = fake_get
        monkeypatch.setattr(asyncio, "sleep", fake_sleep)
        try:
            result: object = asyncio.run(scraper._fetch_list(self.URL))
        except Exception as exc:  # noqa: BLE001 – test kontroluje typ níž
            result = exc
        return calls, sleeps, result

    def test_ceka_dost_dlouho_na_konec_petiminutove_blokace(self, monkeypatch):
        blocked = [self._status_error(self.URL, 429)] * 4
        calls, sleeps, result = self._run(BazosScraper(), [*blocked, LIST_HTML], monkeypatch)
        assert result == LIST_HTML
        assert sum(sleeps) >= 330          # 5½ minuty od první 429
        assert len(calls) == 5             # žádné rychlé opakování po 2 s uvnitř blokace

    def test_po_vycerpani_pokusu_vyjimka(self, monkeypatch):
        import httpx
        blocked = [self._status_error(self.URL, 429)] * 6
        calls, _, result = self._run(BazosScraper(), blocked, monkeypatch)
        assert isinstance(result, httpx.HTTPStatusError)
        assert len(calls) == 6

    def test_necekat_pres_rozpocet_vypisu(self, monkeypatch):
        import time
        import httpx
        scraper = BazosScraper()
        scraper._list_deadline = time.monotonic() + 80    # 30 s se vejde, dalších 60 už ne (zbývá 50)
        blocked = [self._status_error(self.URL, 429)] * 3
        calls, sleeps, result = self._run(scraper, blocked, monkeypatch)
        assert isinstance(result, httpx.HTTPStatusError)
        assert sleeps == [30]
        assert len(calls) == 2

    def test_chyba_serveru_se_opakuje_hned(self, monkeypatch):
        calls, sleeps, result = self._run(
            BazosScraper(), [self._status_error(self.URL, 503), LIST_HTML], monkeypatch)
        assert result == LIST_HTML
        assert len(calls) == 2 and sleeps and max(sleeps) <= 10

    def test_rozpocet_vypisu_necha_cas_na_detaily(self):
        assert BazosScraper.LIST_BUDGET_SECONDS <= BazosScraper.TIME_BUDGET_SECONDS - 10 * 60
