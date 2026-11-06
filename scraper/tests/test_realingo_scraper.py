"""Realingo – výběr inzerátů k detailu, údaje z výpisu a detailu, zamčené nabídky (bez HTTP a DB)."""
import asyncio
import copy
import json
import sys
from contextlib import asynccontextmanager
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))

from core.scrapers import realingo_scraper
from core.scrapers.realingo_scraper import DEFAULT_LISTS, GoneOffer, LockedOffer, RealingoScraper

FIXTURES = Path(__file__).parent / "fixtures" / "realingo"


def _html(data: dict) -> str:
    return f'<html><script id="__NEXT_DATA__" type="application/json">{json.dumps(data)}</script></html>'


def _list_html(offers: list, total: int) -> str:
    return _html({"props": {"pageProps": {"store": {"offer": {"list": {"data": offers, "total": total}}}}}})


def _fixture(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


def _detail_entry(data: dict) -> dict:
    """Vnitřní {id, offer, detail, location} jediného detailu ve fixture."""
    return next(iter(data["props"]["pageProps"]["store"]["offer"]["details"].values()))["offer"]


BORAC_ITEM = {"id": "24755406", "url": "/prodej/dum-rodinny-borac-podoli-u-borace-jihomoravsky-kraj/24755406",
              "district": "Brno-venkov"}


class TestRealingoVypis:

    def test_polozka_nese_id_adresu_cenu_zamek_a_galerii(self):
        html = _list_html([
            {"id": 24735201, "url": "/prodej/dum-rodinny-sentice/24735201", "price": {"total": 6700000},
             "photos": {"main": "offer/aaa/a1", "list": ["offer/aaa/a1", "offer/bbb/b2"]}},
            {"id": 24735202, "url": "/prodej/dum-rodinny-kurim/24735202", "price": {"total": None}, "isLocked": True},
            {"id": 24735203, "url": None},
        ], total=434)
        items, total = RealingoScraper().parse_list_page(html)
        assert total == 434
        assert items == [
            {"id": "24735201", "url": "/prodej/dum-rodinny-sentice/24735201", "price": 6700000.0, "locked": False,
             "photos": ["https://www.realingo.cz/static/images/offer/aaa/a1.webp?w=1600",
                        "https://www.realingo.cz/static/images/offer/bbb/b2.webp?w=1600"]},
            {"id": "24735202", "url": "/prodej/dum-rodinny-kurim/24735202", "price": None, "locked": True,
             "photos": []},
        ]

    def test_skutecny_vypis_zamcena_nabidka_nema_cenu_ani_fotky(self):
        """6. 11. 2026, Brno-venkov domy: čerstvá nabídka z Chudčic je zamčená, ostatní ne."""
        items, total = RealingoScraper().parse_list_page(_html(_fixture("list_brno_venkov_domy.json")))
        by_id = {i["id"]: i for i in items}
        assert total == 460
        assert by_id["24777513"]["locked"] is True
        assert (by_id["24777513"]["price"], by_id["24777513"]["photos"]) == (None, [])
        assert by_id["24774684"]["locked"] is False
        assert by_id["24774684"]["price"] == 5_990_000
        assert len(by_id["24774684"]["photos"]) == 22
        assert by_id["24772219"]["price"] is None          # „cena dohodou“

    def test_brno_venkov_je_ve_vychozich_vypisech(self):
        """6. 10. 2026: portál měl v okrese Brno-venkov 434 domů a nebrali jsme z nich nic."""
        paths = [path for path, _, _ in DEFAULT_LISTS]
        assert "/prodej_domy/Okres_Brno-venkov/" in paths
        assert "/prodej_domy/Okres_Znojmo/" in paths

    @pytest.mark.parametrize("path, expected", [
        ("/prodej_domy/Okres_Znojmo/", "Znojmo"),
        ("/prodej_pozemky/Okres_Brno-venkov/", "Brno-venkov"),
        ("/prodej_byty/Okres_Břeclav/", "Břeclav"),
        ("/prodej_domy/Brno/", ""),
    ])
    def test_okres_z_adresy_vypisu(self, path, expected):
        assert RealingoScraper.district_from_path(path) == expected


class TestRealingoCena:

    @pytest.mark.parametrize("offer, expected", [
        ({"price": {"type": "FIXED", "total": 6290000, "canonical": 6290000}}, 6_290_000),
        ({"price": {"type": "FIXED", "total": 4900000}}, 4_900_000),
        # pozemek za m² – Realingo spočítal celek
        ({"price": {"type": "PER_SQUARE_METER", "canonical": 5542152, "squareMeter": 2999},
          "area": {"main": 1848, "plot": 1848}}, 5_542_152),
        # bez celku: Kč/m² × výměra pozemku, jinak hlavní plocha
        ({"price": {"type": "PER_SQUARE_METER", "squareMeter": 2500}, "area": {"main": 1850, "plot": 1850}}, 4_625_000),
        ({"price": {"type": "PER_SQUARE_METER", "squareMeter": 1500}, "area": {"main": 1385, "plot": None}}, 2_077_500),
        ({"price": {"type": "PER_SQUARE_METER", "squareMeter": 1500}, "area": {}}, None),
        ({"price": {"type": "NEGOTIABLE", "currency": "CZK", "note": "info v RK"}}, None),
        ({"price": {"type": "FIXED", "total": None}, "isLocked": True}, None),
        ({}, None),
    ])
    def test_celkova_cena(self, offer, expected):
        assert RealingoScraper.offer_price(offer) == expected


class TestRealingoDetail:

    def test_odemceny_detail(self):
        listing = RealingoScraper().parse_detail_page(
            _html(_fixture("detail_24755406_borac.json")), BORAC_ITEM, "Dům", "Prodej")
        assert listing is not None
        assert listing["external_id"] == "24755406"
        assert (listing["municipality"], listing["district"]) == ("Borač", "Brno-venkov")
        assert listing["location_text"] == "Borač, okres Brno-venkov"
        assert listing["price"] == 22_500_000
        assert (listing["area_built_up"], listing["area_land"]) == (600, 1800)
        assert len(listing["photos"]) == 20
        assert listing["title"] == "Prodej rodinného domu 600 m², Borač"
        assert listing["description"].startswith("Nabízím výjimečnou")
        assert "price_note" not in listing

    def test_zamceny_detail_se_neulozi_jako_prazdna_kostra(self):
        """Do 7. 11. 2026 z něj vznikl „Prodej rodinného domu“ bez ceny, fotek, popisu a obce,
        s lokalitou „okres Znojmo“ – 358 takových koster bylo aktivních."""
        item = {"id": "24777513", "url": "/prodej/dum-rodinny-chudcice-okres-brno-venkov/24777513",
                "district": "Brno-venkov"}
        with pytest.raises(LockedOffer):
            RealingoScraper().parse_detail_page(_html(_fixture("detail_24777513_locked.json")), item, "Dům", "Prodej")

    def test_detail_bez_casti_detail_je_take_zamceny(self):
        data = _fixture("detail_24755406_borac.json")
        entry = _detail_entry(data)
        entry["detail"] = None
        with pytest.raises(LockedOffer):
            RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej")

    @pytest.mark.parametrize("flag", ["soldOrRented", "deleted"])
    def test_prodano_nebo_smazano(self, flag):
        data = _fixture("detail_24755406_borac.json")
        _detail_entry(data)["offer"][flag] = True
        with pytest.raises(GoneOffer):
            RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej")

    def test_rezervace_drzi_posledni_cenu(self):
        data = _fixture("detail_24755406_borac.json")
        _detail_entry(data)["offer"]["reserved"] = True
        listing = RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej")
        assert (listing["price_note"], listing["keep_last_price"]) == ("Rezervace", True)

    def test_puvod_z_portalu_ktery_bereme_primo_se_preskoci(self):
        data = _fixture("detail_24755406_borac.json")
        _detail_entry(data)["detail"]["externalUrl"] = "https://reality.idnes.cz/detail/prodej/dum/borac/6a0/"
        assert RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej") is None

    def test_okres_bez_drobecku_z_vypisu_ne_znojmo(self):
        data = _fixture("detail_24755406_borac.json")
        _detail_entry(data)["location"] = {"name": "Podolí, Borač", "breadcrumbs": []}
        listing = RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej")
        assert listing["district"] == "Brno-venkov"
        assert listing["location_text"].endswith("okres Brno-venkov")

    def test_bez_polohy_neni_nahradni_okres_znojmo(self):
        data = _fixture("detail_24755406_borac.json")
        _detail_entry(data)["location"] = None
        listing = RealingoScraper().parse_detail_page(
            _html(data), {**BORAC_ITEM, "district": ""}, "Dům", "Prodej")
        assert "Znojmo" not in listing["location_text"]
        assert listing["district"] is None


def _photos(*names: str) -> list:
    return [f"https://www.realingo.cz/static/images/offer/{n}.webp?w=1600" for n in names]


class TestRealingoVyberProDetail:
    ITEMS = [
        {"id": "1", "url": "/a/1", "price": 5_000_000},   # známý, cena stejná
        {"id": "2", "url": "/a/2", "price": 4_500_000},   # známý, zlevnil
        {"id": "3", "url": "/a/3", "price": 3_000_000},   # nový
        {"id": "4", "url": "/a/4", "price": 2_000_000},   # zapamatovaný – bereme ho z iDNES
        {"id": "5", "url": "/a/5", "price": None},        # známý, výpis bez ceny
    ]
    KNOWN = {"1": 5_000_000.0, "2": 4_900_000.0, "5": 1_000_000.0}

    def test_zmena_ceny_napred_pak_nove_zapamatovane_ne(self):
        todo = RealingoScraper.select_for_detail(self.ITEMS, self.KNOWN, {"4"})
        assert [i["id"] for i in todo] == ["2", "3"]

    def test_bez_pameti_se_stahuje_i_drive_preskoceny(self):
        todo = RealingoScraper.select_for_detail(self.ITEMS, self.KNOWN, set())
        assert [i["id"] for i in todo] == ["2", "3", "4"]

    def test_zamcena_nabidka_se_nestahuje(self):
        items = [{"id": "7", "url": "/a/7", "price": None, "locked": True},
                 {"id": "8", "url": "/a/8", "price": None, "locked": True}]
        assert RealingoScraper.select_for_detail(items, {"8": None}, set(), {}, day=0) == []

    def test_znamy_zapamatovany_jako_odjinud_se_nestahuje(self):
        """Kostry uložené ze zamčeného detailu, u kterých se pak ukázalo, že jsou ze Sreality / iDNES."""
        item = {"id": "9", "url": "/a/9", "price": 4_000_000, "photos": _photos("x1")}
        assert RealingoScraper.select_for_detail([item], {"9": None}, {"9"}, {}, day=0) == []

    def test_prenahrana_galerie(self):
        """24611410 (Vratěnín): uložené fotky vrací 404, výpis už nese jiné – cena se nezměnila."""
        item = {"id": "1", "url": "/a/1", "price": 5_000_000, "photos": _photos("new1", "new2")}
        galleries = {"1": set(_photos("old1", "old2"))}
        assert RealingoScraper.select_for_detail([item], self.KNOWN, set(), galleries) == [item]

    def test_pribyla_fotka(self):
        item = {"id": "1", "url": "/a/1", "price": 5_000_000, "photos": _photos("a", "b", "c")}
        assert RealingoScraper.select_for_detail([item], self.KNOWN, set(), {"1": set(_photos("a", "b"))}) == [item]

    def test_kostra_bez_fotek_s_cenou_dohodou(self):
        """Kostra má cenu None a výpis taky (cena dohodou) – změnu pozná až galerie."""
        item = {"id": "5", "url": "/a/5", "price": None, "photos": _photos("a")}
        assert RealingoScraper.select_for_detail([item], self.KNOWN, set(), {}) == [item]

    def test_stejna_galerie_ubrana_fotka_nebo_nezname_ulozene_fotky_ne(self):
        same = {"id": "1", "url": "/a/1", "price": 5_000_000, "photos": _photos("a", "b")}
        removed = {**same, "photos": _photos("a")}
        stored = {"1": set(_photos("a", "b"))}
        assert RealingoScraper.select_for_detail([same], self.KNOWN, set(), stored) == []
        assert RealingoScraper.select_for_detail([removed], self.KNOWN, set(), stored) == []
        assert RealingoScraper.select_for_detail([same], self.KNOWN, set(), None) == []

    def test_rotace_az_za_novymi_a_zmenami(self):
        known = {str(n): 1_000_000.0 for n in range(100, 140)}
        items = [{"id": i, "url": f"/a/{i}", "price": 1_000_000} for i in known]
        items += [{"id": "new", "url": "/a/new", "price": 2_000_000},
                  {"id": "100", "url": "/a/100x", "price": 900_000}]   # duplicitní položka se změnou ceny
        day = 739_000
        todo = RealingoScraper.select_for_detail(items, known, set(), None, day=day)
        rotated = [i["id"] for i in todo[2:]]
        assert [i["id"] for i in todo[:2]] == ["100", "new"]
        assert rotated == [i for i in known if RealingoScraper.in_rotation(i, day)]
        assert 0 < len(rotated) < len(known)


class TestRotace:

    def test_kazdy_inzerat_prave_jednou_za_tyden(self):
        ids = [str(24_600_000 + n) for n in range(700)]
        start = 739_000
        for external_id in ids:
            hits = [d for d in range(start, start + RealingoScraper.ROTATION_DAYS)
                    if RealingoScraper.in_rotation(external_id, d)]
            assert len(hits) == 1

    def test_denni_davky_jsou_vyrovnane(self):
        ids = [str(24_600_000 + n) for n in range(1_100)]
        per_day = [sum(RealingoScraper.in_rotation(i, d) for i in ids) for d in range(739_000, 739_007)]
        assert sum(per_day) == len(ids)
        assert max(per_day) < 1.3 * len(ids) / RealingoScraper.ROTATION_DAYS

    def test_hash_je_stabilni_mezi_procesy(self):
        """crc32, ne hash() – ten je v každém procesu jiný a rotace by se rozpadla."""
        assert [d for d in range(7) if RealingoScraper.in_rotation("24611410", d)] == [1_005_951_544 % 7]


# ── celý běh s podvrženou DB a HTTP ──────────────────────────────────────────

class _FakeDb:
    def __init__(self, known: dict, skipped: set, galleries: dict, rejected: set):
        self.known, self.skipped, self.galleries, self.rejected = known, skipped, galleries, rejected
        self.touched: list = []
        self.deactivated: list = []
        self.saved: list = []
        self.remembered: list = []

    async def get_known_prices(self, source_code):
        return dict(self.known)

    async def get_skipped_ids(self, source_code):
        return set(self.skipped)

    @asynccontextmanager
    async def acquire(self):
        db = self

        class _Conn:
            async def fetch(self, sql, source_code):
                return [{"external_id": k, "urls": list(v)} for k, v in db.galleries.items()]
        yield _Conn()

    async def touch_listings(self, source_code, items):
        self.touched.extend(external_id for external_id, _, _ in items)
        return len(items)

    async def deactivate_listing(self, source_code, external_id):
        self.deactivated.append(external_id)
        return True

    async def mark_active_seen(self, source_code):
        return 0

    async def remember_skipped(self, source_code, external_ids, reason):
        self.remembered.extend(external_ids)

    async def upsert_listing(self, listing):
        self.saved.append(listing["external_id"])
        return None if listing["external_id"] in self.rejected else f"uuid-{listing['external_id']}"


def _offer(offer_id: str, price, photos=("p",), locked=False) -> dict:
    return {"id": offer_id, "url": f"/prodej/dum-rodinny-x/{offer_id}", "isLocked": locked,
            "price": {"type": "FIXED", "total": price}, "area": {"main": 100, "plot": 500},
            "photos": {"main": None, "list": [f"offer/{offer_id}/{p}" for p in photos]}}


def _detail_html(offer: dict, external_url=None, locked=False) -> str:
    entry = {"id": offer["id"], "offer": offer,
             "detail": None if locked else {"id": offer["id"], "externalUrl": external_url, "description": "Popis"},
             "location": None if locked else {"name": "Šanov", "breadcrumbs": [{"name": "Okres Znojmo"}, {"name": "Šanov"}]}}
    return _html({"props": {"pageProps": {"store": {"offer": {"details": {offer["id"]: {"offer": entry}}}}}}})


class TestRealingoBeh:
    """Kostry z doby, kdy se zamčené detaily ukládaly: co se z nich po odemčení stane."""

    def _run(self, monkeypatch, offers, details, known, skipped=frozenset(), galleries=None, rejected=frozenset(),
             budget=None):
        db = _FakeDb(known, set(skipped), galleries or {}, set(rejected))
        monkeypatch.setattr(realingo_scraper, "get_db_manager", lambda: db)
        scraper = RealingoScraper(lists=[("/prodej_domy/Okres_Znojmo/", "Dům", "Prodej")])
        scraper.DETAIL_DELAY_SECONDS = 0
        # Den, kdy žádný známý inzerát není v rotaci – výběr pak určují jen ceny, galerie a zámky
        scraper.today = lambda: next(d for d in range(7) if not any(scraper.in_rotation(k, d) for k in known))
        if budget is not None:
            scraper.TIME_BUDGET_SECONDS = budget
        fetched: list = []

        async def fake_fetch(url: str) -> str:
            fetched.append(url)
            if "/prodej_domy/" in url:
                return _list_html(offers, total=len(offers))
            return details[url.rsplit("/", 1)[-1]]

        scraper._fetch = fake_fetch
        result = asyncio.run(scraper.scrape(max_pages=1))
        return scraper, db, fetched, result

    def test_kostry_po_odemceni(self, monkeypatch):
        offers = [
            _offer("10", 5_000_000),                 # známý, beze změny → jen viděn
            _offer("11", 4_000_000),                 # kostra, detail OK → uloží se celý
            _offer("12", 3_000_000),                 # kostra, původ ze Sreality → deaktivovat, zapamatovat
            _offer("13", 22_500_000),                # kostra, nad cenový limit → deaktivovat
            _offer("14", 2_000_000),                 # zapamatovaný jako odjinud, ale uložený → deaktivovat
            _offer("15", None, (), locked=True),     # nový zamčený → nestahovat, neukládat
            _offer("16", None, (), locked=True),     # kostra pořád zamčená → jen viděn
            _offer("17", 1_500_000),                 # nový → uložit
        ]
        details = {o["id"]: _detail_html(o) for o in offers}
        details["12"] = _detail_html(offers[2], external_url="https://www.sreality.cz/detail/prodej/dum/x/1")
        known = {"10": 5_000_000.0, "11": None, "12": None, "13": None, "14": None, "16": None}
        galleries = {"10": {"https://www.realingo.cz/static/images/offer/10/p.webp?w=1600"}}

        scraper, db, fetched, result = self._run(
            monkeypatch, offers, details, known, skipped={"14"}, galleries=galleries, rejected={"13"})

        fetched_details = [u.rsplit("/", 1)[-1] for u in fetched if "/prodej/" in u]
        assert fetched_details == ["11", "12", "13", "17"]
        assert db.saved == ["11", "13", "17"]
        assert sorted(db.deactivated) == ["12", "13", "14"]
        assert sorted(db.touched) == ["10", "16"]
        assert db.remembered == ["12"]
        assert (scraper.locked, scraper.skipped_origin, scraper.retired) == (0, 1, 3)
        assert result == 2 + 3        # viděné známé + zpracované detaily (i ten vyřazený filtrem)

    def test_nestihnute_detaily_zname_zustanou_videne(self, monkeypatch):
        """Vybrané známé se neoznačují předem – co nestihne rozpočet, musí se označit na konci,
        jinak by je runner po plném běhu deaktivoval."""
        offers = [_offer("20", 1_000_000), _offer("21", 1_100_000), _offer("22", 900_000)]
        details = {o["id"]: _detail_html(o) for o in offers}
        known = {"20": 2_000_000.0, "21": 2_000_000.0, "22": None}

        scraper, db, fetched, result = self._run(monkeypatch, offers, details, known, budget=0)

        assert not [u for u in fetched if "/prodej/" in u]
        assert sorted(db.touched) == ["20", "21", "22"]
        assert db.deactivated == []
        assert result == 3

    def test_detail_zamceny_i_kdyz_vypis_ne(self, monkeypatch):
        offers = [_offer("30", 1_000_000)]
        details = {"30": _detail_html(offers[0], locked=True)}

        scraper, db, fetched, result = self._run(monkeypatch, offers, details, {"30": None})

        assert db.saved == [] and db.deactivated == []
        assert db.touched == ["30"]
        assert scraper.locked == 1

    def test_nove_zamcene_a_vyrazene_filtrem_se_nedeaktivuji(self, monkeypatch):
        offers = [_offer("40", 50_000_000)]
        details = {"40": _detail_html(copy.deepcopy(offers[0]))}

        scraper, db, fetched, result = self._run(monkeypatch, offers, details, {}, rejected={"40"})

        assert db.saved == ["40"]
        assert db.deactivated == [] and db.touched == []



def test_gps_z_offer_location_jen_presna():
    """7. 11. 2026: GPS je v offer.location, ne offer.gps; střed obce (TOWN) se nebere."""
    data = _fixture("detail_24755406_borac.json")
    location = _detail_entry(data)["offer"]["location"]
    assert location["locationPrecision"] == "TOWN"
    town = RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej")
    assert "latitude" not in town

    location["locationPrecision"] = "STREET"
    street = RealingoScraper().parse_detail_page(_html(data), BORAC_ITEM, "Dům", "Prodej")
    assert (street["latitude"], street["longitude"]) == (49.400655833333, 16.365490833333)
