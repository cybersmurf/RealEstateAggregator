"""Nové nahrání fotek zdrojem – klasifikace se nepřenáší podle pořadí, galerie se doklasifikuje."""
from datetime import datetime

from core.database import _gallery_needs_reclassification


def _row(id_: str, url: str, idx: int, classified: bool = True) -> dict:
    return {"id": id_, "original_url": url, "order_index": idx,
            "classified_at": datetime(2026, 9, 21) if classified else None, "stored_url": None}


class TestGalleryNeedsReclassification:
    def test_vsechny_url_nove_po_novem_nahrani_fotek(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0), _row("b", "https://cdn/c/2.jpg", 1)]

        assert _gallery_needs_reclassification(existing, ["https://cdn/d/2.jpg", "https://cdn/d/1.jpg"])

    def test_pribyla_fotka_ke_klasifikovane_galerii(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0)]

        assert _gallery_needs_reclassification(existing, ["https://cdn/c/1.jpg", "https://cdn/c/2.jpg"])

    def test_neklasifikovana_galerie_se_do_fronty_nedava(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0, classified=False)]

        assert not _gallery_needs_reclassification(existing, ["https://cdn/d/1.jpg"])

    def test_beze_zmeny_nebo_jen_zmena_poradi(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0), _row("b", "https://cdn/c/2.jpg", 1)]

        assert not _gallery_needs_reclassification(existing, ["https://cdn/c/2.jpg", "https://cdn/c/1.jpg"])

    def test_ubyla_fotka(self):
        existing = [_row("a", "https://cdn/c/1.jpg", 0), _row("b", "https://cdn/c/2.jpg", 1)]

        assert not _gallery_needs_reclassification(existing, ["https://cdn/c/1.jpg"])

    def test_novy_inzerat_bez_fotek_v_db(self):
        assert not _gallery_needs_reclassification([], ["https://cdn/c/1.jpg"])
