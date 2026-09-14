"""Tests for photo_file_stem in database.py"""
import sys
import os
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(__file__))))

from scraper.core.database import photo_file_stem


def test_stem_is_stable_for_same_url():
    url = "https://www.eurobydleni.cz/rozhrani/uploads/company/6140/10172705/1786710285_DB8UZqJub5.jpg"
    assert photo_file_stem(url) == photo_file_stem(url)


def test_stem_differs_for_different_urls():
    a = photo_file_stem("https://cdn.example.cz/10172705/1786710285_DB8UZqJub5.jpg")
    b = photo_file_stem("https://cdn.example.cz/10172705/1787309265_v6km1OU08B.jpg")
    assert a != b


def test_stem_is_filesystem_safe():
    stem = photo_file_stem("https://cdn.example.cz/foto?id=1&size=big/../x y.jpg")
    assert len(stem) == 16
    assert all(c in "0123456789abcdef" for c in stem)
