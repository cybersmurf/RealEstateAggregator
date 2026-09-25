"""
Parametry dražby z textu inzerátu (titulek + popis).

Čisté regex funkce bez DB – volá je `_enrich_auction_fields` v core/database.py
a jako fallback SReality scraper, když detail nemá strukturované položky.

Vytahujeme:
  • termín dražby      – "12. 10. 2026", "12.10.2026", volitelně čas "v 10:00" / "10:00 hod"
  • vyvolávací cenu    – "vyvolávací cena", "nejnižší podání", "vyvolávací"
  • dražební jistotu   – "dražební jistota", "jistota", "kauce" (jen v kontextu dražby)
"""
import re
from datetime import datetime, timezone
from typing import Optional, Tuple
from zoneinfo import ZoneInfo

PRAGUE = ZoneInfo("Europe/Prague")

# Kontext dražby – "dražba", "dražbě", "dražební", "aukce", "aukční", "vydražit", …
_RE_AUCTION_CONTEXT = re.compile(r"dra[žz](?:b|ebn)|aukc|vydra[žz]", re.IGNORECASE)

# Explicitní označení nabídky jako dražby (pro přepnutí offer_type)
_RE_AUCTION_OFFER = re.compile(
    r"\b(?:nedobrovoln[áa]|dobrovoln[áa]|elektronick[áa]|ve[řr]ejn[áa]|exeku[čc]n[íi])?\s*dra[žz]b[aěyu]\b",
    re.IGNORECASE,
)

# Částka: "1 250 000", "1.250.000", "1250000", "2,5 mil.", "2 mil. Kč", "500 tis."
_AMOUNT = r"(\d{1,3}(?:[  .]\d{3})+|\d+(?:[,.]\d+)?)\s*(mil\.?|mil(?:i[oó]n[ůuy]?)?|tis\.?|tis[íi]c[eů]?)?\s*(?:K[čc]|CZK|,-)?"

_RE_STARTING_PRICE = re.compile(
    r"(?:vyvol[áa]vac[íi]\s+cen[aouy]|nejni[žz][šs][íi]\s+pod[áa]n[íi]|vyvol[áa]vac[íi])"
    r"[^\d]{0,40}?" + _AMOUNT,
    re.IGNORECASE,
)

_RE_DEPOSIT = re.compile(
    r"(?:dra[žz]ebn[íi]\s+jistot[aouy]|jistot[aouy]|kauc[eií])"
    r"[^\d]{0,40}?" + _AMOUNT,
    re.IGNORECASE,
)

# Datum: "12. 10. 2026", "12.10.2026", "12. října 2026"; volitelný čas "v 10:00", "10:00 hod", "od 10.00 hodin"
_MONTHS = {
    "ledna": 1, "února": 2, "unora": 2, "března": 3, "brezna": 3, "dubna": 4, "května": 5, "kvetna": 5,
    "června": 6, "cervna": 6, "července": 7, "cervence": 7, "srpna": 8, "září": 9, "zari": 9,
    "října": 10, "rijna": 10, "listopadu": 11, "prosince": 12,
}
_MONTH_ALT = "|".join(sorted(_MONTHS, key=len, reverse=True))

_RE_DATE = re.compile(
    r"(?<!\d)(\d{1,2})\.\s*(?:(\d{1,2})\.|(" + _MONTH_ALT + r"))\s*(20\d{2})(?!\d)"
    r"(?:[^\d\n]{0,25}?(?:v|od|ve)?\s*(\d{1,2})[:.](\d{2})\s*(?:hod(?:in)?\.?|h\b)?)?",
    re.IGNORECASE,
)

_RE_AUCTION_DATE_CONTEXT = re.compile(
    r"(?:dra[žz]b[aěyu]\s+(?:se\s+)?(?:kon[áa]|prob[ěe]hne|bude|za[čc][íi]n[áa]|je\s+na[řr][íi]zen[áa])|"
    r"term[íi]n\s+(?:konání\s+)?dra[žz]by|datum\s+(?:konání\s+)?dra[žz]by|zah[áa]jen[íi]\s+dra[žz]by|"
    r"dra[žz]ebn[íi]\s+jedn[áa]n[íi]|dra[žz]b[aěyu]|aukc[eií])",
    re.IGNORECASE,
)


def is_auction_context(text: str) -> bool:
    """Text vůbec mluví o dražbě/aukci (jinak "jistota"/"kauce" znamená nájemní kauci)."""
    return bool(text) and bool(_RE_AUCTION_CONTEXT.search(text))


def mentions_auction_offer(text: str) -> bool:
    """Text výslovně označuje nabídku jako dražbu ("nedobrovolná dražba", "elektronická dražba", …)."""
    return bool(text) and bool(_RE_AUCTION_OFFER.search(text))


def _amount_to_float(number: str, unit: Optional[str]) -> Optional[float]:
    raw = number.replace(" ", " ")
    if re.fullmatch(r"\d{1,3}(?:[ .]\d{3})+", raw):
        value = float(re.sub(r"[ .]", "", raw))
    else:
        value = float(raw.replace(",", "."))

    unit_l = (unit or "").lower()
    if unit_l.startswith("mil"):
        value *= 1_000_000
    elif unit_l.startswith("tis"):
        value *= 1_000

    if value < 1_000:
        return None  # "vyvolávací cena 1" apod. je šum
    return float(int(round(value)))


def parse_auction_starting_price(text: str) -> Optional[float]:
    """Vyvolávací cena / nejnižší podání v Kč, nebo None."""
    if not text:
        return None
    m = _RE_STARTING_PRICE.search(text)
    if not m:
        return None
    return _amount_to_float(m.group(1), m.group(2))


def parse_auction_deposit(text: str) -> Optional[float]:
    """Dražební jistota v Kč, nebo None. "jistota"/"kauce" bereme jen v kontextu dražby."""
    if not text or not is_auction_context(text):
        return None
    m = _RE_DEPOSIT.search(text)
    if not m:
        return None
    return _amount_to_float(m.group(1), m.group(2))


def _build_datetime(day: int, month: int, year: int, hour: int, minute: int) -> Optional[datetime]:
    try:
        local = datetime(year, month, day, hour, minute, tzinfo=PRAGUE)
    except ValueError:
        return None
    return local.astimezone(timezone.utc)


def _date_candidates(text: str) -> list:
    """Všechna data v textu jako (pozice, datetime UTC)."""
    result = []
    for m in _RE_DATE.finditer(text):
        day = int(m.group(1))
        if m.group(2):
            month = int(m.group(2))
        else:
            month = _MONTHS.get(m.group(3).lower(), 0)
        year = int(m.group(4))
        hour = int(m.group(5)) if m.group(5) else 0
        minute = int(m.group(6)) if m.group(6) else 0
        if not (1 <= month <= 12) or hour > 23 or minute > 59:
            continue
        dt = _build_datetime(day, month, year, hour, minute)
        if dt is not None:
            result.append((m.start(), dt))
    return result


def parse_auction_date(text: str) -> Optional[datetime]:
    """
    Termín dražby jako timezone-aware datetime v UTC (zadáno v Europe/Prague), nebo None.

    Preferuje datum, které následuje do ~120 znaků za zmínkou o dražbě
    ("dražba se koná 12. 10. 2026 v 10:00"); bez takové vazby bere první datum
    v textu, ale jen když text o dražbě mluví.
    """
    if not text or not is_auction_context(text):
        return None

    candidates = _date_candidates(text)
    if not candidates:
        return None

    for ctx in _RE_AUCTION_DATE_CONTEXT.finditer(text):
        for pos, dt in candidates:
            if ctx.end() <= pos <= ctx.end() + 120:
                return dt

    return candidates[0][1]


def parse_auction_fields(text: str) -> Tuple[Optional[datetime], Optional[float], Optional[float]]:
    """(auction_date, auction_starting_price, auction_deposit) – vše volitelné."""
    return (
        parse_auction_date(text),
        parse_auction_starting_price(text),
        parse_auction_deposit(text),
    )
