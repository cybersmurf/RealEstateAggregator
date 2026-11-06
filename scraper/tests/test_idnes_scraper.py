"""iDNES – rotace detailů známých inzerátů a fotky jen z galerie (bez HTTP a DB)."""
import sys
from pathlib import Path

import pytest
from bs4 import BeautifulSoup

sys.path.insert(0, str(Path(__file__).parent.parent))

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
