"""iDNES – rotace detailů známých inzerátů a fotky jen z galerie (bez HTTP a DB)."""
import asyncio
import sys
from pathlib import Path

import pytest
from bs4 import BeautifulSoup

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers import idnes_reality_scraper
from core.scrapers.idnes_reality_scraper import IdnesRealityScraper

THUMB = "https://sta-reality2.1gr.cz/sta/compile/thumbs"


def _item(external_id: str, price) -> dict:
    return {"url": f"https://reality.idnes.cz/detail/prodej/dum/znojmo/{external_id}/", "external_id": external_id,
            "location_text": "Znojmo", "municipality": "Znojmo", "district": "Znojmo", "price": price}


class TestIdnesRotace:
    KNOWN_IDS = [f"6a{n:022x}" for n in range(60)]

    def test_bez_dne_jen_nove_a_zmeny_ceny(self):
        items = [_item("a1", 5_000_000), _item("a2", 4_000_000), _item("a3", 3_000_000)]
        known = {"a1": 5_000_000.0, "a2": 4_500_000.0}
        assert [i["external_id"] for i in IdnesRealityScraper.select_for_detail(items, known)] == ["a2", "a3"]

    def test_rotace_az_za_zmenami_a_novymi(self):
        known = {i: 1_000_000.0 for i in self.KNOWN_IDS}
        items = [_item(i, 1_000_000) for i in self.KNOWN_IDS] + [_item("novy", 2_000_000)]
        items[0] = _item(self.KNOWN_IDS[0], 900_000)            # zlevnil
        day = 739_001

        todo = IdnesRealityScraper.select_for_detail(items, known, day=day)

        assert [i["external_id"] for i in todo[:2]] == [self.KNOWN_IDS[0], "novy"]
        rotated = todo[2:]
        assert [i["external_id"] for i in rotated] == [
            i for i in self.KNOWN_IDS[1:] if IdnesRealityScraper.in_rotation(i, day)]
        assert all(i["rotation"] for i in rotated)
        assert not any(i.get("rotation") for i in todo[:2])
        assert 0 < len(rotated) < len(self.KNOWN_IDS)

    def test_znamy_s_cenou_na_dotaz_prijde_na_radu_v_rotaci(self):
        item = _item("c1", None)
        day = next(d for d in range(7) if IdnesRealityScraper.in_rotation("c1", d))
        assert IdnesRealityScraper.select_for_detail([item], {"c1": 5_000_000.0}, day=day) == [{**item, "rotation": True}]
        assert IdnesRealityScraper.select_for_detail([item], {"c1": 5_000_000.0}, day=day + 1) == []

    def test_kazdy_inzerat_prave_jednou_za_tyden(self):
        for external_id in self.KNOWN_IDS:
            days = [d for d in range(739_000, 739_000 + IdnesRealityScraper.ROTATION_DAYS)
                    if IdnesRealityScraper.in_rotation(external_id, d)]
            assert len(days) == 1

    def test_denni_davky_jsou_vyrovnane(self):
        ids = [f"6a{n:022x}" for n in range(2_800)]
        per_day = [sum(IdnesRealityScraper.in_rotation(i, d) for i in ids) for d in range(739_000, 739_007)]
        assert sum(per_day) == len(ids)
        assert max(per_day) < 1.2 * len(ids) / IdnesRealityScraper.ROTATION_DAYS

    def test_rotace_se_vejde_do_rozpoctu(self):
        """Sedmina z ~2 800 známých při ~1 s na detail + výpisy ~6 min se musí vejít do rozpočtu."""
        rotation = 2_800 // IdnesRealityScraper.ROTATION_DAYS
        assert rotation + 200 <= IdnesRealityScraper.MAX_DETAILS_PER_RUN
        assert 6 * 60 + (rotation + 200) * 1.0 < IdnesRealityScraper.TIME_BUDGET_SECONDS / 2


class TestIdnesCenaZVypisu:

    def test_detail_bez_ceny_vezme_cenu_z_vypisu(self):
        """Upsert cenu přepisuje – obnova detailu v rotaci by jinak smazala známou cenu."""
        normalized = {"title": "Prodej domu 120 m²", "price": None}
        IdnesRealityScraper.apply_list_item(normalized, _item("d1", 4_990_000.0))
        assert normalized["price"] == 4_990_000.0

    def test_cena_z_detailu_ma_prednost(self):
        normalized = {"title": "Prodej domu 120 m²", "price": 4_800_000}
        IdnesRealityScraper.apply_list_item(normalized, _item("d1", 4_990_000.0))
        assert normalized["price"] == 4_800_000

    def test_rezervace_bez_ceny_nechava_posledni(self):
        normalized = {"title": "Prodej domu 120 m²rezervováno", "price": None}
        IdnesRealityScraper.apply_list_item(normalized, _item("d1", None))
        assert normalized["price"] is None
        assert (normalized["price_note"], normalized["keep_last_price"]) == ("Rezervace", True)


class TestIdnesFotkyInzeratu:
    """6. 11. 2026: no-image-gallery.png a map-marker.png byly ve fotkách 20 a 19 aktivních inzerátů,
    video-play.png v 6 – uložené před opravou galerie (20. 10.) a od té doby bez čerstvého detailu."""

    @pytest.mark.parametrize("url, expected", [
        (f"{THUMB}/e/a/6/2460bccf1f160a07dd41017454029.jpg", True),
        ("https://reality.idnes.cz/file/thumbnail/6aee19d8f1993ee1130b0b61?profile=front_detail_article_big", True),
        ("https://sta-reality2.1gr.cz/ui/image/no-image-gallery.png", False),
        ("https://sta-reality2.1gr.cz/ui/image/gallery/map-marker.png", False),
        ("https://sta-reality2.1gr.cz/ui/image/gallery/video-play.png", False),
        (f"{THUMB}/video/6a43cca3dae0256ac7072a97.mp4", False),
        ("https://www.example.com/logo-rk.png", False),
        ("/sta/compile/thumbs/e/a/6/relative.jpg", False),
        ("", False),
    ])
    def test_je_fotka_inzeratu(self, url, expected):
        assert IdnesRealityScraper.is_listing_photo(url) is expected

    def test_galerie_bez_grafiky_portalu_a_videa(self):
        html = f"""
        <div class="b-gallery carousel"><div class="carousel__list">
          <a data-fancybox="images" href="{THUMB}/0/c/9/b25.jpg?gt=r">
            <span><img src="{THUMB}/e/a/6/first.jpg"></span>
            <span class="b-gallery__play"><img src="https://sta-reality2.1gr.cz/ui/image/gallery/video-play.png"></span>
          </a>
          <a data-fancybox="images" data-fancybox-video="" href="{THUMB}/video/6a43cca3.mp4">
            <img src="{THUMB}/e/a/6/video-poster.jpg">
          </a>
          <a data-fancybox="images"><img src="https://sta-reality2.1gr.cz/ui/image/no-image-gallery.png"></a>
          <a data-fancybox="images"><img data-lazy="{THUMB}/2/5/f/second.jpg"
             src="https://sta-reality2.1gr.cz/ui/image/no-image-gallery.png"></a>
          <a data-fancybox="images"><img src="https://sta-reality2.1gr.cz/ui/image/gallery/map-marker.png"></a>
          <a data-fancybox="images"><img data-lazy="https://reality.idnes.cz/file/thumbnail/6aee19d8?profile=front_detail_article_big"
             src="https://sta-reality2.1gr.cz/ui/image/no-image-gallery.png"></a>
        </div></div>"""
        photos = IdnesRealityScraper.extract_gallery_photos(BeautifulSoup(html, "html.parser"))
        assert photos == [
            f"{THUMB}/e/a/6/first.jpg",
            f"{THUMB}/2/5/f/second.jpg",
            "https://reality.idnes.cz/file/thumbnail/6aee19d8?profile=front_detail_article_big",
        ]

    def test_jen_placeholder_bez_fotek_nevrati_nic(self):
        html = """<html><head><meta property="og:image" content="https://sta-reality2.1gr.cz/ui/image/no-image-gallery.png">
        </head><body><div class="b-gallery"><a data-fancybox="images">
        <img src="https://sta-reality2.1gr.cz/ui/image/no-image-gallery.png"></a></div></body></html>"""
        assert IdnesRealityScraper.extract_gallery_photos(BeautifulSoup(html, "html.parser")) == []


# ── celý běh s podvrženou DB a HTTP ──────────────────────────────────────────

class _FakeDb:
    def __init__(self, known: dict, rejected: set) -> None:
        self.known, self.rejected = known, rejected
        self.touched: list = []
        self.deactivated: dict = {}
        self.saved: list = []

    async def get_known_prices(self, source_code):
        assert source_code == "IDNES"
        return dict(self.known)

    async def touch_listings(self, source_code, items):
        self.touched.extend(external_id for external_id, _, _ in items)
        return len(items)

    async def deactivate_listing(self, source_code, external_id, reason=None):
        self.deactivated[external_id] = reason
        return True

    async def mark_active_seen(self, source_code):
        return 0

    async def upsert_listing(self, listing):
        self.saved.append(listing["external_id"])
        return None if listing["external_id"] in self.rejected else f"uuid-{listing['external_id']}"


def _detail_html(price: str = "5 000 000 Kč") -> str:
    return (f"<html><body><h1>Prodej domu 120 m²</h1><div class='b-detail__price'>{price}</div>"
            f"<div class='b-detail__desc'>Popis domu</div></body></html>")


class TestIdnesBehFiltry:
    """7. 11. 2026: známý inzerát mimo filtry (ve výpisu i po čerstvém detailu) zůstával aktivní se
    starými daty – výpis ho každou noc označil jako viděný."""

    def _run(self, monkeypatch, items, known, rejected=frozenset(), details=None, budget=None):
        db = _FakeDb(known, set(rejected))
        monkeypatch.setattr(idnes_reality_scraper, "get_db_manager", lambda: db)

        async def _no_sleep(_seconds):
            return None

        monkeypatch.setattr(idnes_reality_scraper.asyncio, "sleep", _no_sleep)
        scraper = IdnesRealityScraper()
        # Den, kdy žádný známý inzerát není v rotaci – výběr určují jen ceny
        scraper.today = lambda: next(d for d in range(7) if not any(scraper.in_rotation(k, d) for k in known))
        if budget is not None:
            monkeypatch.setattr(IdnesRealityScraper, "TIME_BUDGET_SECONDS", budget)   # out_of_time je classmethod
        fetched: list = []

        async def fake_items(max_list_pages):
            return [dict(i) for i in items]

        async def fake_fetch(url):
            fetched.append(url)
            html = (details or {}).get(IdnesRealityScraper._extract_external_id(url), _detail_html())
            if html is None:
                raise RuntimeError("HTTP 503")
            return html

        scraper._fetch_district_items = fake_items
        scraper._fetch_page = fake_fetch
        result = asyncio.run(scraper.scrape(max_list_pages=1))
        fetched_ids = [IdnesRealityScraper._extract_external_id(u) for u in fetched]
        return scraper, db, fetched_ids, result

    def test_znamy_nad_limit_ve_vypisu_se_deaktivuje_bez_detailu(self, monkeypatch):
        items = [_item("a1", 12_500_000), _item("a2", 5_000_000), _item("a3", 13_000_000)]
        scraper, db, fetched, result = self._run(monkeypatch, items, {"a1": 9_500_000.0, "a2": 5_000_000.0})

        assert fetched == []                      # „a3" je nový mimo filtry, „a1" známý mimo filtry
        assert db.deactivated == {"a1": "out of search filters (list)"}
        assert db.touched == ["a2"]
        assert (scraper.retired, result) == (1, 1)

    def test_znamy_vyrazeny_detailem_se_deaktivuje_a_neoznaci(self, monkeypatch):
        items = [_item("b1", 9_900_000), _item("b2", 4_000_000)]
        details = {"b1": _detail_html("10 900 000 Kč")}
        scraper, db, fetched, result = self._run(monkeypatch, items, {"b1": 9_000_000.0, "b2": 4_500_000.0},
                                                 rejected={"b1"}, details=details)

        assert fetched == ["b1", "b2"]
        assert db.saved == ["b1", "b2"]
        assert db.deactivated == {"b1": "out of search filters (detail)"}
        assert db.touched == []                   # vybrané k detailu se předem neoznačují
        assert result == 1

    def test_nestihnute_nebo_chybne_detaily_zname_zustanou_videne(self, monkeypatch):
        items = [_item("c1", 4_000_000), _item("c2", 3_000_000)]
        scraper, db, fetched, result = self._run(monkeypatch, items, {"c1": 4_500_000.0, "c2": 3_500_000.0},
                                                 details={"c1": None})
        assert fetched == ["c1", "c2"]
        assert db.touched == ["c1"]               # chyba detailu → označen na konci
        assert db.deactivated == {}
        assert result == 2

        scraper, db, fetched, result = self._run(monkeypatch, items, {"c1": 4_500_000.0, "c2": 3_500_000.0},
                                                 budget=0)
        assert fetched == []
        assert sorted(db.touched) == ["c1", "c2"]
        assert result == 2

    def test_novy_vyrazeny_detailem_se_nedeaktivuje(self, monkeypatch):
        scraper, db, fetched, result = self._run(monkeypatch, [_item("d1", 5_000_000)], {}, rejected={"d1"})
        assert fetched == ["d1"]
        assert db.deactivated == {} and db.touched == []
        assert result == 0
