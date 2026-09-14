"""
Plochy nemovitosti z volného textu (titulek, popis) – sdílené pro všechny scrapery.

Detekce duplikátů i cenový signál stojí na plochách; 7 zdrojů je dřív neukládalo vůbec,
přestože je měly v titulku ("Prodej rodinného domu, 81 m², Tvořihráz", "ZP 246 m², zahrada 350 m²").
"""
import re
from typing import Optional, Tuple

# Mezera jako oddělovač tisíců jen před trojicí číslic ("1 809 m²") a číslo nesmí
# navazovat na dispozici – "3+1 75 m²" jinak dá 175, "4+1 120 m²" 1120.
_AREA_NUMBER = r"(?<![\d+/])(\d{1,3}(?:[\s ]\d{3})+|\d+)"
_M2 = r"\s*m(?:[²2]|\s*<sup>2</sup>)"

_AREA_RE = re.compile(_AREA_NUMBER + _M2)
# "pozemek 800 m²", "na pozemku 1510 m²", "zahrada 350 m²", "stavební parcely 1362 m2",
# "pozemky o celkové výměře 820 m2"
_LAND_RE = re.compile(
    r"(?:pozem\w*|zahrad\w*|parcel\w*)[\s ]+(?:o[\s ]+(?:\w+[\s ]+)?(?:výměře|velikosti|ploše)[\s ]+)?"
    + _AREA_NUMBER + _M2,
    re.IGNORECASE,
)

# Titulek, který nabízí pozemek (ne dům se zahradou) – pro zdroje, co typ pošlou jako "Ostatní"
_LAND_TITLE_RE = re.compile(
    r"\b(?:pozem\w*|parcel\w*|zahrad[ayu]\b|pole\b|polí\b|louk\w*|les\w*\b|lesní|vinic\w*|sad\w*\b|orn[áé]\w*|zemědělsk\w*)",
    re.IGNORECASE,
)
_BUILDING_TITLE_RE = re.compile(
    r"\b(?:dům|domu|domy|rd\b|vil\w*|chat\w*|chalup\w*|byt\w*|garáž\w*|komerč\w*|prostor\w*|kancel\w*|sklad\w*|hal\w*|objekt\w*)",
    re.IGNORECASE,
)

_MIN_AREA, _MAX_AREA = 10, 1_000_000


def _to_int(raw: str) -> Optional[int]:
    digits = re.sub(r"\D", "", raw)
    if not digits:
        return None
    value = int(digits)
    return value if _MIN_AREA <= value <= _MAX_AREA else None


def parse_title_areas(title: str) -> Tuple[Optional[int], Optional[int]]:
    """(užitná/zastavěná, pozemek) z titulku; chybějící hodnota = None."""
    if not title:
        return None, None

    land_match = _LAND_RE.search(title)
    land = _to_int(land_match.group(1)) if land_match else None

    usable: Optional[int] = None
    for m in _AREA_RE.finditer(title):
        # Přeskoč číslo patřící k "pozemek X m²" (a vše za ním)
        if land_match and m.start(1) >= land_match.start(1):
            continue
        usable = _to_int(m.group(1))
        if usable:
            break

    return usable, land


def parse_description_land(description: str) -> Optional[int]:
    """Výměra pozemku z popisu ("dům stojí na pozemku o celkové výměře 820 m2")."""
    if not description:
        return None
    m = _LAND_RE.search(description)
    return _to_int(m.group(1)) if m else None


def title_offers_land(title: str) -> bool:
    """Titulek nabízí pozemek – "Prodej stavební parcely 1362 m2", ne "Dům se zahradou"."""
    return bool(title) and bool(_LAND_TITLE_RE.search(title)) and not _BUILDING_TITLE_RE.search(title)
