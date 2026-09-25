"""Parametry dražby z textu (core/auction_parsing.py) a jejich doplnění v _enrich_listing_fields."""
import sys
from datetime import datetime, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.auction_parsing import (
    is_auction_context,
    mentions_auction_offer,
    parse_auction_date,
    parse_auction_deposit,
    parse_auction_fields,
    parse_auction_starting_price,
)
from core.database import _enrich_listing_fields

PRAGUE = ZoneInfo("Europe/Prague")


def _utc(year: int, month: int, day: int, hour: int = 0, minute: int = 0) -> datetime:
    """Lokální pražský čas → UTC, stejně jako to dělá parser."""
    return datetime(year, month, day, hour, minute, tzinfo=PRAGUE).astimezone(timezone.utc)


class TestParseAuctionDate:
    @pytest.mark.parametrize("text, expected", [
        ("Dražba se koná 12. 10. 2026 v 10:00 hod.", _utc(2026, 10, 12, 10, 0)),
        ("Elektronická dražba proběhne dne 12.10.2026 od 10:00 hodin.", _utc(2026, 10, 12, 10, 0)),
        ("Termín dražby: 3. 2. 2027, 13.30 hod", _utc(2027, 2, 3, 13, 30)),
        ("Dražba 12. října 2026 v 9:00", _utc(2026, 10, 12, 9, 0)),
        ("Nedobrovolná dražba rodinného domu, zahájení dražby 5. 6. 2026", _utc(2026, 6, 5)),
    ])
    def test_dates_with_auction_context(self, text, expected):
        assert parse_auction_date(text) == expected

    def test_result_is_timezone_aware_utc(self):
        dt = parse_auction_date("Dražba se koná 12. 10. 2026 v 10:00")
        assert dt is not None
        assert dt.tzinfo is timezone.utc
        # Říjen = letní čas (UTC+2) → 10:00 Praha = 08:00 UTC
        assert (dt.hour, dt.minute) == (8, 0)

    def test_winter_time_offset(self):
        dt = parse_auction_date("Dražba se koná 15. 1. 2027 v 10:00")
        assert dt is not None
        # Leden = UTC+1 → 10:00 Praha = 09:00 UTC
        assert dt.hour == 9

    def test_prefers_date_after_auction_mention(self):
        text = ("Dům postaven v roce 2005, kolaudace 1. 6. 2006. "
                "Dražba se koná 20. 11. 2026 v 11:00. Prohlídky 10. 11. 2026.")
        assert parse_auction_date(text) == _utc(2026, 11, 20, 11, 0)

    @pytest.mark.parametrize("text", [
        "Prodej rodinného domu, kolaudace 12. 10. 2006.",  # bez dražby
        "Dražba se koná v říjnu 2026.",                      # bez konkrétního dne
        "Dražba 32. 13. 2026",                               # neplatné datum
        "",
    ])
    def test_no_date(self, text):
        assert parse_auction_date(text) is None


class TestParseStartingPrice:
    @pytest.mark.parametrize("text, expected", [
        ("Vyvolávací cena: 1 250 000 Kč", 1_250_000),
        ("Nejnižší podání činí 2.450.000,- Kč", 2_450_000),
        ("vyvolávací cena 850000 Kč", 850_000),
        ("Vyvolávací cena 2,5 mil. Kč", 2_500_000),
        ("Nejnižší podání 3 mil. Kč, dražební jistota 300 tis. Kč", 3_000_000),
        ("Vyvolávací: 990 000", 990_000),
        ("Nejnižší podání je stanoveno na částku 1 890 000 Kč", 1_890_000),
    ])
    def test_amounts(self, text, expected):
        assert parse_auction_starting_price(text) == expected

    @pytest.mark.parametrize("text", [
        "Cena 4 500 000 Kč",              # běžná cena, ne vyvolávací
        "Vyvolávací cena bude upřesněna",  # bez částky
        "",
    ])
    def test_no_price(self, text):
        assert parse_auction_starting_price(text) is None


class TestParseDeposit:
    @pytest.mark.parametrize("text, expected", [
        ("Dražba. Dražební jistota: 150 000 Kč", 150_000),
        ("Elektronická dražba, jistota 200.000 Kč", 200_000),
        ("Dražba domu, kauce 50 tis. Kč", 50_000),
        ("Nejnižší podání 3 mil. Kč, dražební jistota 300 tis. Kč", 300_000),
    ])
    def test_amounts(self, text, expected):
        assert parse_auction_deposit(text) == expected

    def test_rental_deposit_is_ignored_without_auction_context(self):
        # "kauce" u pronájmu není dražební jistota
        assert parse_auction_deposit("Pronájem bytu 2+kk, kauce 30 000 Kč") is None


class TestContext:
    @pytest.mark.parametrize("text, expected", [
        ("Nedobrovolná dražba rodinného domu", True),
        ("Elektronická dražba pozemku", True),
        ("Prodej bytu v aukci", False),   # aukce ≠ výslovná dražba
        ("Prodej rodinného domu", False),
    ])
    def test_mentions_auction_offer(self, text, expected):
        assert mentions_auction_offer(text) is expected

    @pytest.mark.parametrize("text, expected", [
        ("Prodej bytu v aukci", True),
        ("Byt bude vydražen", True),
        ("Prodej rodinného domu", False),
    ])
    def test_is_auction_context(self, text, expected):
        assert is_auction_context(text) is expected


class TestParseAuctionFields:
    def test_full_text(self):
        text = ("Nedobrovolná elektronická dražba rodinného domu 4+1 v Miroslavi. "
                "Dražba se koná 12. 10. 2026 v 10:00 hod. na portálu. "
                "Nejnižší podání: 1 250 000 Kč. Dražební jistota: 150 000 Kč.")
        assert parse_auction_fields(text) == (_utc(2026, 10, 12, 10, 0), 1_250_000, 150_000)

    def test_plain_sale_yields_nothing(self):
        assert parse_auction_fields("Prodej RD 4+1, 106 m², Znojmo. Cena 5 500 000 Kč.") == (None, None, None)


class TestEnrichListingFields:
    def test_auction_offer_gets_fields(self):
        data = {
            "title": "Dražba rodinného domu, Znojmo",
            "description": "Dražba se koná 12. 10. 2026 v 10:00. Nejnižší podání 1 250 000 Kč. Dražební jistota 150 000 Kč.",
            "offer_type": "Dražba",
        }
        _enrich_listing_fields(data)
        assert data["auction_date"] == _utc(2026, 10, 12, 10, 0)
        assert data["auction_starting_price"] == 1_250_000
        assert data["auction_deposit"] == 150_000
        assert data["offer_type"] == "Dražba"

    def test_sale_switches_to_auction_when_text_says_so(self):
        data = {
            "title": "Rodinný dům 4+1, Hrušovany nad Jevišovkou",
            "description": "Nedobrovolná dražba. Vyvolávací cena 990 000 Kč.",
            "offer_type": "Prodej",
        }
        _enrich_listing_fields(data)
        assert data["offer_type"] == "Dražba"
        assert data["auction_starting_price"] == 990_000

    def test_sale_stays_sale_without_starting_price(self):
        data = {
            "title": "Rodinný dům 4+1",
            "description": "Dům byl získán v dražbě v roce 2015, nyní prodej.",
            "offer_type": "Prodej",
        }
        _enrich_listing_fields(data)
        assert data["offer_type"] == "Prodej"
        assert "auction_starting_price" not in data

    def test_scraper_values_are_not_overwritten(self):
        data = {
            "title": "Dražba bytu",
            "description": "Nejnižší podání 1 000 000 Kč.",
            "offer_type": "Dražba",
            "auction_starting_price": 1_500_000.0,
        }
        _enrich_listing_fields(data)
        assert data["auction_starting_price"] == 1_500_000.0

    def test_plain_sale_untouched(self):
        data = {"title": "Prodej RD 4+1, 106 m²", "description": "Cena 5 500 000 Kč.", "offer_type": "Prodej"}
        _enrich_listing_fields(data)
        assert "auction_date" not in data
        assert data["offer_type"] == "Prodej"
