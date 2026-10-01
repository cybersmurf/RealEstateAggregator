"""Rotace CDN URL fotek (Sreality) – klasifikace se přenese na novou URL místo mrtvého řádku."""
from datetime import datetime

from core.database import _match_rotated_photos


def _row(id_: str, url: str, idx: int, classified: bool = True) -> dict:
    return {"id": id_, "original_url": url, "order_index": idx,
            "classified_at": datetime(2026, 9, 21) if classified else None, "stored_url": None}


class TestMatchRotatedPhotos:
    def test_stejny_pocet_fotek_nove_url_na_stejnem_indexu(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0), _row("b", "https://cdn/c/2.jpg", 1)]
        new = ["https://cdn/d/1.jpg", "https://cdn/d/2.jpg"]

        assert _match_rotated_photos(existing, new) == {"a": ("https://cdn/d/1.jpg", 0), "b": ("https://cdn/d/2.jpg", 1)}

    def test_neklasifikovane_fotky_se_neprenaseji(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0, classified=False)]

        assert _match_rotated_photos(existing, ["https://cdn/d/1.jpg"]) == {}

    def test_jiny_pocet_fotek_znamena_posun_poradi_a_neparuje_se(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0), _row("b", "https://cdn/c/2.jpg", 1)]
        new = ["https://cdn/d/0.jpg", "https://cdn/d/1.jpg", "https://cdn/d/2.jpg"]

        assert _match_rotated_photos(existing, new) == {}

    def test_zname_url_se_nepovazuji_za_nove(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0), _row("b", "https://cdn/c/2.jpg", 1)]
        new = ["https://cdn/c/1.jpg", "https://cdn/d/2.jpg"]

        assert _match_rotated_photos(existing, new) == {"b": ("https://cdn/d/2.jpg", 1)}

    def test_beze_zmeny_vraci_prazdno(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0)]

        assert _match_rotated_photos(existing, ["https://cdn/c/1.jpg"]) == {}

    def test_duplicitni_radky_na_stejnem_indexu_nepocitaji_se_dvakrat(self):
        # Stav po dřívější rotaci: starý mrtvý řádek + nový na indexu 0; zdroj vrací 1 fotku
        existing = [_row("old", "https://cdn/c/1.jpg", 0), _row("new", "https://cdn/d/1.jpg", 0, classified=False)]

        assert _match_rotated_photos(existing, ["https://cdn/d/1.jpg"]) == {}
