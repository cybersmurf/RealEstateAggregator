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
