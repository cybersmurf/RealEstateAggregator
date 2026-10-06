"""
Okres z názvu obce – záloha pro inzeráty, které nemají okres ani GPS.

Zdroje jako Prodejme.to ("Suchohrdly, Jihomoravský kraj") nebo iDNES ("Jirice U Miroslavi",
"Pohorelice Znojemska") posílají jen název místa. Okres se dohledá ve slovníku obec → okres
sestaveném z inzerátů Sreality (tam je obojí spolehlivé); obce, které leží ve více okresech
(Hrušky – Břeclav i Vyškov), ve slovníku nejsou a zůstanou bez okresu.
Inzeráty s GPS řeší přesněji tabulka re_realestate.districts (hranice okresů).
"""
import re
import unicodedata
from typing import Dict, Optional

_SEPARATORS_RE = re.compile(r"[,\n–—]|\s-\s")
_MAX_PREFIX_WORDS = 4


def normalize_place(value: str) -> str:
    """Malá písmena, bez diakritiky, jedna mezera – "Jiřice u  Miroslavi" → "jirice u miroslavi"."""
    decomposed = unicodedata.normalize("NFD", value.lower())
    plain = "".join(ch for ch in decomposed if unicodedata.category(ch) != "Mn")
    return " ".join(plain.split())


def district_from_place_names(
    location_text: Optional[str], municipality: Optional[str], known: Dict[str, str]
) -> Optional[str]:
    """
    Okres podle názvu obce. `known` = normalizovaný název obce → okres (jen jednoznačné).

    Zkouší postupně: pole municipality, části location_text oddělené čárkou/pomlčkou
    ("Havránkova, Brno" → Brno) a nakonec začátek textu po slovech od nejdelšího
    ("Pohorelice Znojemska" → Pohořelice, "Jirice U Miroslavi" → Jiřice u Miroslavi).
    """
    if municipality:
        district = known.get(normalize_place(municipality))
        if district:
            return district

    if not location_text:
        return None

    parts = [normalize_place(p) for p in _SEPARATORS_RE.split(location_text)]
    parts = [p for p in parts if p]
    for part in parts:
        district = known.get(part)
        if district:
            return district

    # Bez oddělovačů: "Obec Ulice" – název obce je na začátku
    if len(parts) == 1:
        words = parts[0].split()
        for size in range(min(len(words), _MAX_PREFIX_WORDS), 0, -1):
            district = known.get(" ".join(words[:size]))
            if district:
                return district

    return None
