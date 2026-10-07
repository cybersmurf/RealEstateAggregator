"""Century 21 – fotky z Next.js payloadu a okres z vyhledávání (bez HTTP a DB)."""
import sys
from pathlib import Path

from bs4 import BeautifulSoup

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers.century21_scraper import SEARCH_CONFIGS, Century21Scraper

HTML = """<html><body>
<img src="https://live-file-api.igluu.cz/file/0f243ab0-5c2e-40dc-bbd9-4efaa550e3a2?isPublic=true">
<img src="https://live-file-api.igluu.cz/thumb/not-a-uuid.jpg">
<script>self.__next_f.push([1,"\\"images\\":[\\"https://live-file-api.igluu.cz/file/124f7da7-539a-4dea-85cd-f0a3f3c844c6?isPublic=true\\",\\"https://live-file-api.igluu.cz/file/0f243ab0-5c2e-40dc-bbd9-4efaa550e3a2?isPublic=true\\",\\"https://live-file-api.igluu.cz/file/167eadc7-a86d-452f-a97c-4e4302f207fd?isPublic=true\\"]"])</script>
</body></html>"""


def test_fotky_z_img_i_z_next_payloadu_bez_duplicit():
    photos = Century21Scraper()._extract_photos(BeautifulSoup(HTML, "html.parser"), HTML)
    assert photos == [
        "https://live-file-api.igluu.cz/file/0f243ab0-5c2e-40dc-bbd9-4efaa550e3a2?isPublic=true",
        "https://live-file-api.igluu.cz/file/124f7da7-539a-4dea-85cd-f0a3f3c844c6?isPublic=true",
        "https://live-file-api.igluu.cz/file/167eadc7-a86d-452f-a97c-4e4302f207fd?isPublic=true",
    ]


def test_bez_surového_html_vezme_aspon_img():
    photos = Century21Scraper()._extract_photos(BeautifulSoup(HTML, "html.parser"))
    assert photos[0].endswith("0f243ab0-5c2e-40dc-bbd9-4efaa550e3a2?isPublic=true")
    assert len(photos) == 3


def test_kazde_vyhledavani_ma_jediny_okres():
    assert {tuple(c["county"]) for c in SEARCH_CONFIGS} == {("Znojmo",), ("Brno-venkov",), ("Břeclav",)}
    assert len(SEARCH_CONFIGS) == 21
