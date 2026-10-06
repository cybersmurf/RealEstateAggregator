"""
Database utilities for scraper.
Provides async connection pool and CRUD operations for listings.
"""
import hashlib
import os
import re
import time
import asyncpg
import httpx
import logging
from pathlib import Path
from typing import Optional, Dict, Any, List, Mapping, Sequence, Tuple
from uuid import UUID, uuid4
from datetime import datetime, timedelta
from contextlib import asynccontextmanager

# Cesta k lokálnímu úložišti fotek (sdílený volume s .NET API)
# API ukládá do /app/wwwroot/uploads, scraper do /app/uploads — oba na stejném Docker volume.
# Výsledný stored_url = "/uploads/listings/{id}/photos/{hash original_url}.jpg"
_UPLOADS_BASE_PATH: Optional[Path] = None


def photo_file_stem(photo_url: str) -> str:
    """Název souboru fotky (bez přípony) – stabilní a jedinečný pro každou original_url."""
    return hashlib.sha1(photo_url.encode("utf-8")).hexdigest()[:16]

def _get_uploads_base_path() -> Optional[Path]:
    """Vrátí base path pro lokální ukládání fotek, nebo None pokud není nakonfigurováno."""
    global _UPLOADS_BASE_PATH
    if _UPLOADS_BASE_PATH is None:
        env_path = os.environ.get("UPLOADS_BASE_PATH", "")
        if env_path:
            _UPLOADS_BASE_PATH = Path(env_path)
    return _UPLOADS_BASE_PATH

# HTTP klient sdílený pro inline photo download (timeout 15s na fotku)
_PHOTO_DOWNLOAD_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
    "Accept": "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
}

from .filters import get_filter_manager
from .area_parsing import parse_title_areas, parse_description_land, title_offers_land
from .auction_parsing import is_auction_context, mentions_auction_offer, parse_auction_fields


# ── Regex enrichment ──────────────────────────────────────────────────────────
_RE_DISPOSITION = re.compile(r'(\d+\+(?:\d+|kk))', re.IGNORECASE)
_RE_CONDITION_MAP = [
    (re.compile(r'novostavb|ve v\xfdstavb|pod kl\xed\u010d|developersk\xfd projekt', re.IGNORECASE), 'Novostavba'),
    (re.compile(r'po kompletn\xed rekonstrukci|po celkov\xe9 rekonstrukci|po rekonstrukci|kompletn\u011b zrekon', re.IGNORECASE), 'Po rekonstrukci'),
    (re.compile(r'p\u0159ed rekonstrukc\xed|k rekonstrukci|vy\u017eaduje rekonstrukci|pot\u0159ebuje rekonstrukci', re.IGNORECASE), 'P\u0159ed rekonstrukc\xed'),
    (re.compile(r'k demolici|velmi \u0161patn\xfd stav|havarijní', re.IGNORECASE), 'K demolici'),
    (re.compile(r'zachoval\xfd stav|dobr\xfd stav|udr\u017eovan\xfd stav|v dobr\xe9m stavu', re.IGNORECASE), 'Dobr\xfd stav'),
]
# Negace před klíčovým slovem stavu: "není novostavba", "nejde o rekonstrukci", "bez rekonstrukce"
_RE_NEGATED_BEFORE = re.compile(r'(?:\bnení|\bnejde o|\bnikoli|\bne\b|\bbez)\s*(?:\w+\s+)?$', re.IGNORECASE)
_RE_CONSTRUCTION_MAP = [
    (re.compile(r'cihlová|cihlový|ciheln|cihla|z cihel', re.IGNORECASE), 'Cihla'),
    (re.compile(r'panel[oá]|panelový d\u016fm|panelová budova', re.IGNORECASE), 'Panel'),
    # Jen když je ze dřeva STAVBA – samotné „dřevěn" chytalo podlahy, okna, trámy a parkety
    # (6. 10. 2026: ze 484 aktivních „Dřevo" byla dřevostavba, srub nebo dřevěná chata jen u 104).
    (re.compile(
        r'd\u0159evostav|\bsrub\w*|rouben\w*'
        r'|d\u0159ev\u011bn\w+\s+(?:\w+\s+)?(?:stavb|d\u016fm|dom[ue]k?\b|domk|chat|chalup|konstrukc|objekt|budov|novostavb|rodinn)'
        r'|(?:d\u016fm|domek|stavba|chata|konstrukce|objekt)\s+(?:je\s+)?(?:\w+\s+)?ze\s+d\u0159eva',
        re.IGNORECASE), 'D\u0159evo'),
    (re.compile(r'montovan|prefabrik\xe1t|skelet', re.IGNORECASE), 'Montovaná'),
    (re.compile(r'\bzd\u011bn[a\xe1\xe9\xfd]\b', re.IGNORECASE), 'Zděná'),
]


# PSČ → okres (Jihomoravský kraj + okolí). Bazoš, iDnes a menší realitky posílají jen
# "671 71 Znojmo" bez okresu, takže 900+ inzerátů vypadávalo z lokalitních statistik a filtrů.
_POSTAL_DISTRICTS = {
    "669": "Znojmo", "671": "Znojmo",
    "664": "Brno-venkov", "665": "Brno-venkov", "667": "Brno-venkov",
    "600": "Brno-město", "602": "Brno-město", "603": "Brno-město", "612": "Brno-město", "613": "Brno-město",
    "614": "Brno-město", "615": "Brno-město", "616": "Brno-město", "617": "Brno-město", "618": "Brno-město",
    "619": "Brno-město", "620": "Brno-město", "621": "Brno-město", "623": "Brno-město", "624": "Brno-město",
    "625": "Brno-město", "627": "Brno-město", "628": "Brno-město", "634": "Brno-město", "635": "Brno-město",
    "636": "Brno-město", "637": "Brno-město", "638": "Brno-město", "639": "Brno-město", "641": "Brno-město",
    "642": "Brno-město", "643": "Brno-město", "644": "Brno-město",
    "678": "Blansko", "679": "Blansko",
    "683": "Vyškov", "684": "Vyškov", "685": "Vyškov",
    "690": "Břeclav", "691": "Břeclav", "692": "Břeclav",
    "693": "Hodonín", "695": "Hodonín", "696": "Hodonín",
}
_RE_POSTAL = re.compile(r'\b(\d{3})\s?\d{2}\b')
_DISTRICT_KEYWORDS = [
    ("znojm", "Znojmo"),
    ("brno-venkov", "Brno-venkov"),
    ("brno-město", "Brno-město"), ("brno-mesto", "Brno-město"),
    ("břeclav", "Břeclav"), ("hodonín", "Hodonín"), ("vyškov", "Vyškov"), ("blansko", "Blansko"),
]


def enrich_district(data: Dict[str, Any]) -> None:
    """Doplní 'district' z PSČ nebo z klíčových slov v location_text, když ho scraper nedodal."""
    if data.get("district"):
        return
    text = " ".join(filter(None, [data.get("location_text", ""), data.get("municipality", "")]))
    m = _RE_POSTAL.search(text)
    if m and m.group(1) in _POSTAL_DISTRICTS:
        data["district"] = _POSTAL_DISTRICTS[m.group(1)]
        return
    low = text.lower()
    for kw, district in _DISTRICT_KEYWORDS:
        if kw in low:
            data["district"] = district
            return


def _enrich_listing_fields(data: Dict[str, Any]) -> None:
    """
    Doplní chybějící sémantická pole (disposition, rooms, condition, construction_type)
    regex extrakcí z title + description.
    Volá se automaticky v upsert_listing() pro všechny scrapers.
    Pokud scraper pole už vyplnil, ponechá stávající hodnotu.
    """
    text = ' '.join(filter(None, [data.get('title', ''), data.get('description', '')]))

    # disposition + rooms
    if not data.get('disposition'):
        m = _RE_DISPOSITION.search(text)
        if m:
            data['disposition'] = m.group(1).upper().replace('KK', 'KK')
    if not data.get('rooms') and data.get('disposition'):
        rm = re.match(r'^(\d+)', data['disposition'])
        if rm:
            data['rooms'] = int(rm.group(1))

    # condition – klíčové slovo nesmí být negované ("dům není novostavba", "nejde o novostavbu")
    if not data.get('condition'):
        for pattern, value in _RE_CONDITION_MAP:
            if any(not _RE_NEGATED_BEFORE.search(text[max(0, m.start() - 20):m.start()]) for m in pattern.finditer(text)):
                data['condition'] = value
                break

    # construction_type
    if not data.get('construction_type'):
        for pattern, value in _RE_CONSTRUCTION_MAP:
            if pattern.search(text):
                data['construction_type'] = value
                break

    _enrich_areas(data)
    _enrich_auction_fields(data)
    enrich_district(data)


_AUCTION_OFFER_TYPES = {'Dražba', 'Auction'}


def _enrich_auction_fields(data: Dict[str, Any]) -> None:
    """
    Parametry dražby (termín, vyvolávací cena, jistota) z titulku + popisu.

    Spouští se, když je nabídka označená jako dražba, nebo když o dražbě/aukci mluví text.
    Hodnoty, které už scraper dodal (např. SReality z items[]), nepřepisuje.
    Když nabídka dražbou označená není, ale text ji tak výslovně pojmenuje
    ("nedobrovolná dražba", "elektronická dražba") a najdeme vyvolávací cenu,
    přepneme offer_type na "Dražba" – jinak by dražby ze zdrojů bez kategorie
    padaly mezi běžné prodeje.
    """
    text = ' '.join(filter(None, [data.get('title', ''), data.get('description', '')]))
    is_auction = data.get('offer_type') in _AUCTION_OFFER_TYPES

    if not is_auction and not is_auction_context(text):
        return

    auction_date, starting_price, deposit = parse_auction_fields(text)

    if data.get('auction_date') is None and auction_date is not None:
        data['auction_date'] = auction_date
    if data.get('auction_starting_price') is None and starting_price is not None:
        data['auction_starting_price'] = starting_price
    if data.get('auction_deposit') is None and deposit is not None:
        data['auction_deposit'] = deposit

    if not is_auction and mentions_auction_offer(text) and data.get('auction_starting_price') is not None:
        data['offer_type'] = 'Dražba'


_LAND_TYPES = {'Pozemek', 'Land'}
_BUILDING_TYPES = {'Dům', 'House', 'Chata', 'Cottage'}
_UNKNOWN_TYPES = {'Ostatní', 'Other', None, ''}


def _enrich_areas(data: Dict[str, Any]) -> None:
    """
    Plochy a typ pozemku z titulku/popisu, když je scraper nedodal.

    7 zdrojů (NEMZNOJMO, CENTURY21, PRODEJMETO, MMR, LEXAMO, DELUXREALITY, HVREALITY)
    neukládalo plochy vůbec a pozemky posílalo jako "Ostatní" – detekce duplikátů
    je pak se SREALITY nemohla spárovat (jiný typ, nic k porovnání).
    """
    title = data.get('title') or ''

    if data.get('property_type') in _UNKNOWN_TYPES and title_offers_land(title):
        data['property_type'] = 'Pozemek'

    usable, land = parse_title_areas(title)
    # Stejné číslo, jaké už scraper určil jako pozemek, není zároveň plocha domu
    # („…sklepem a zahradou, 1223 m²" – Bazoš, Šatov)
    if not data.get('area_built_up') and usable and usable != data.get('area_land'):
        data['area_built_up'] = usable
    if not data.get('area_land') and land:
        data['area_land'] = land

    if not data.get('area_land') and data.get('property_type') in _BUILDING_TYPES:
        desc_land = parse_description_land(data.get('description') or '')
        if desc_land:
            data['area_land'] = desc_land

    # U pozemku je jediná známá plocha jeho výměra ("Prodej zahrady 1 809 m²")
    if data.get('property_type') in _LAND_TYPES and data.get('area_built_up'):
        if not data.get('area_land'):
            data['area_land'] = data['area_built_up']
        if data['area_land'] == data['area_built_up']:
            data['area_built_up'] = None

logger = logging.getLogger(__name__)


def _gallery_needs_reclassification(existing: Sequence[Mapping[str, Any]], photo_urls: Sequence[str]) -> bool:
    """
    Galerie, která už byla klasifikovaná, a zdroj do ní poslal fotku s neznámou URL.

    Klasifikace patří obrázku, ne pozici: nová URL je pro nás nová fotka, i když přišla na místo
    staré. Sreality i Lexamo při novém nahrání fotek makléřem změní všechny URL a často i pořadí,
    takže přenos podle order_index přilepil popis obýváku k bazénu (Dyje, 6. 10. 2026). Novou fotku
    proto necháme neklasifikovanou a runner po scrapu požádá API o doklasifikování galerie.
    """
    if not any(row["classified_at"] is not None for row in existing):
        return False
    known = {row["original_url"] for row in existing}
    return any(url not in known for url in photo_urls)


class DatabaseManager:
    """Manages PostgreSQL connection pool for scraper."""
    
    def __init__(self, host: str, port: int, database: str, user: str, password: str, 
                 min_size: int = 5, max_size: int = 20, source_cache_ttl_seconds: int = 3600):
        self.host = host
        self.port = port
        self.database = database
        self.user = user
        self.password = password
        self.min_size = min_size
        self.max_size = max_size
        self._pool: Optional[asyncpg.Pool] = None
        
        # 🔥 Source code caching: {source_code: (data, timestamp)}
        self._source_cache: Dict[str, tuple[Dict[str, Any], datetime]] = {}
        self._cache_ttl = timedelta(seconds=source_cache_ttl_seconds)
        # Inzeráty, jejichž klasifikovaná galerie dostala nové fotky – runner je po scrapu pošle API
        self._galleries_to_reclassify: set[UUID] = set()
    
    async def connect(self) -> None:
        """Create connection pool."""
        if self._pool is not None:
            logger.warning("Database pool already exists")
            return
        
        try:
            self._pool = await asyncpg.create_pool(
                host=self.host,
                port=self.port,
                database=self.database,
                user=self.user,
                password=self.password,
                min_size=self.min_size,
                max_size=self.max_size,
            )
            logger.info(f"Database pool connected to {self.host}:{self.port}/{self.database}")
        except Exception as exc:
            logger.exception(f"Failed to create database pool: {exc}")
            raise
    
    async def disconnect(self) -> None:
        """Close connection pool."""
        if self._pool is not None:
            await self._pool.close()
            self._pool = None
            logger.info("Database pool closed")
        
        # Vyčisti cache
        self._source_cache.clear()
    
    @asynccontextmanager
    async def acquire(self):
        """Acquire connection from pool."""
        if self._pool is None:
            raise RuntimeError("Database pool not initialized. Call connect() first.")
        
        async with self._pool.acquire() as conn:
            yield conn
    
    async def get_source_by_code(self, source_code: str) -> Optional[Dict[str, Any]]:
        """
        Získá source (zdroj) podle kódu s in-memory caching.
        
        Cachuje resultat po dobu cache_ttl (default 1 hodina).
        
        Args:
            source_code: Kód zdroje (např. "REMAX", "MMR")
            
        Returns:
            Dict se source daty nebo None
        """
        # 🔥 Kontrola cache
        if source_code in self._source_cache:
            cached_data, cached_at = self._source_cache[source_code]
            if datetime.utcnow() - cached_at < self._cache_ttl:
                logger.debug(f"Cache HIT for source {source_code}")
                return cached_data
            else:
                # Cache expired
                del self._source_cache[source_code]
                logger.debug(f"Cache EXPIRED for source {source_code}")
        
        # Načti z databáze
        async with self.acquire() as conn:
            row = await conn.fetchrow(
                """
                SELECT id, code, name, base_url, is_active
                FROM re_realestate.sources
                WHERE code = $1
                """,
                source_code
            )
            if row:
                data = dict(row)
                # 🔥 Ulož do cache
                self._source_cache[source_code] = (data, datetime.utcnow())
                logger.debug(f"Cache STORE for source {source_code}")
                return data
            return None
    
    async def upsert_listing(self, listing_data: Dict[str, Any]) -> Optional[UUID]:
        """
        Upsert listing do databáze (atomicky bez race condition).
        
        Pokud listing s daným (source_id, external_id) již existuje, aktualizuje ho.
        Pokud neexistuje, vytvoří nový.
        
        Používá PostgreSQL ON CONFLICT DO UPDATE pattern - je atomická a bezpečná
        i při souběžných insertů se stejným external_id.
        
        Kontroluje searchovací filtry - pokud inzerát nedodpovídá kritériím,
        nebude vložen do DB.
        
        Args:
            listing_data: Dictionary s daty listingu
            
        Returns:
            UUID listingu (nového nebo existujícího) nebo None pokud je vyloučen filtry
        """
        # Doplň chybějící sémantická pole regex extrakcí
        _enrich_listing_fields(listing_data)

        # Okres z GPS nebo názvu obce, když ho scraper neposlal – MUSÍ být před filtrem lokality:
        # ten hledá okres v textu a „ulice Dlouhá, Hrabětice" bez okresu zahodil (audit 6. 10. 2026:
        # RE/MAX, Premia Reality, Nemovitosti Znojmo a Znojmo Reality tak ztrácely vesnické nabídky).
        await self._derive_district(listing_data)

        # 🔥 Kontrola filtrů
        filter_mgr = get_filter_manager()
        should_include, exclusion_reason = filter_mgr.should_include_listing(listing_data)
        
        if not should_include:
            filter_mgr.log_listing_decision(listing_data, False, exclusion_reason)
            logger.debug(f"Skipped listing due to filter: {exclusion_reason}")
            return None
        
        # Získej source_id podle source_code
        source = await self.get_source_by_code(listing_data["source_code"])
        if not source:
            raise ValueError(f"Source '{listing_data['source_code']}' not found in database")
        
        source_id = source["id"]
        source_name = source["name"]
        external_id = listing_data.get("external_id")
        listing_id = uuid4()
        
        # Mapování českých hodnot na enum hodnoty v DB
        property_type_map = {
            # České hodnoty (většina scraperů)
            "Dům": "House",
            "Byt": "Apartment",
            "Pozemek": "Land",
            "Chata": "Cottage",
            "Komerční": "Commercial",
            "Průmyslový": "Industrial",
            "Garáž": "Garage",
            "Ostatní": "Other",
            # Anglické passthrough (REAS a budoucí scrapery)
            "House": "House",
            "Apartment": "Apartment",
            "Land": "Land",
            "Cottage": "Cottage",
            "Commercial": "Commercial",
            "Industrial": "Industrial",
            "Garage": "Garage",
            "Other": "Other",
        }
        
        offer_type_map = {
            # České hodnoty
            "Prodej": "Sale",
            "Pronájem": "Rent",
            "Dražba": "Auction",
            # Anglické passthrough (REAS a budoucí scrapery)
            "Sale": "Sale",
            "Rent": "Rent",
            "Auction": "Auction",
        }
        
        property_type_db = property_type_map.get(listing_data.get("property_type", "Ostatní"), "Other")
        offer_type_db = offer_type_map.get(listing_data.get("offer_type", "Prodej"), "Sale")
        
        now = datetime.utcnow()
        
        async with self.acquire() as conn:
            # 🔥 Načti stávající cenu a plochy před upsertem – pro detekci změny ceny
            # a pro invalidaci AI cenového signálu, jehož vstupem je Kč/m²
            old_row = await conn.fetchrow(
                """
                SELECT price, area_built_up, area_land
                FROM re_realestate.listings WHERE source_id = $1 AND external_id = $2
                """,
                source_id, external_id
            )
            old_price = old_row["price"] if old_row else None

            # Rezervovaná nabídka: zdroj cenu nahradil štítkem – necháme poslední známou
            # (ze sloupce, případně z historie cen, pokud ji dřívější scrape už vynuloval)
            if listing_data.get("keep_last_price") and listing_data.get("price") is None and old_row is not None:
                last_price = old_price
                if last_price is None:
                    last_price = await conn.fetchval(
                        """
                        SELECT h.price FROM re_realestate.listing_price_history h
                        JOIN re_realestate.listings l ON l.id = h.listing_id
                        WHERE l.source_id = $1 AND l.external_id = $2 AND h.price IS NOT NULL
                        ORDER BY h.recorded_at DESC LIMIT 1
                        """,
                        source_id, external_id
                    )
                if last_price is not None:
                    listing_data["price"] = float(last_price)

            # 🔥 ATOMIC UPSERT s ON CONFLICT DO UPDATE
            # Žádné race conditions - DB se postará o atomicitu
            result = await conn.fetchval(
                """
                INSERT INTO re_realestate.listings (
                    id, source_id, source_code, source_name, external_id, url,
                    title, description, property_type, offer_type, price,
                    location_text, area_built_up, area_land,
                    disposition, rooms, condition, construction_type,
                    latitude, longitude, geocoded_at, geocode_source,
                    view_count, date_created_source,
                    first_seen_at, last_seen_at, is_active,
                    district, municipality,
                    auction_date, auction_starting_price, auction_deposit,
                    seller_name, seller_email, seller_phone, seller_company,
                    price_note
                )
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14,
                        $15, $16, $17, $18, $19, $20, $21, $22, $23, $24, $25, $26, true,
                        $27, $28, $29, $30, $31, $32, $33, $34, $35, $36)
                ON CONFLICT (source_id, external_id) DO UPDATE
                SET
                    -- Poznámka k ceně („Rezervace") se přepisuje vždy – po uvolnění rezervace zmizí
                    price_note        = EXCLUDED.price_note,
                    url               = EXCLUDED.url,
                    title             = EXCLUDED.title,
                    description       = EXCLUDED.description,
                    property_type     = EXCLUDED.property_type,
                    offer_type        = EXCLUDED.offer_type,
                    price             = EXCLUDED.price,
                    location_text     = EXCLUDED.location_text,
                    area_built_up     = EXCLUDED.area_built_up,
                    area_land         = EXCLUDED.area_land,
                    disposition       = COALESCE(EXCLUDED.disposition, re_realestate.listings.disposition),
                    rooms             = COALESCE(EXCLUDED.rooms,       re_realestate.listings.rooms),
                    condition         = COALESCE(EXCLUDED.condition,   re_realestate.listings.condition),
                    construction_type = COALESCE(EXCLUDED.construction_type, re_realestate.listings.construction_type),
                    latitude = COALESCE(EXCLUDED.latitude, re_realestate.listings.latitude),
                    longitude = COALESCE(EXCLUDED.longitude, re_realestate.listings.longitude),
                    geocoded_at = CASE
                        WHEN EXCLUDED.latitude IS NOT NULL THEN EXCLUDED.geocoded_at
                        ELSE re_realestate.listings.geocoded_at
                    END,
                    geocode_source = CASE
                        WHEN EXCLUDED.latitude IS NOT NULL THEN EXCLUDED.geocode_source
                        ELSE re_realestate.listings.geocode_source
                    END,
                    view_count          = COALESCE(EXCLUDED.view_count, re_realestate.listings.view_count),
                    date_created_source = COALESCE(re_realestate.listings.date_created_source, EXCLUDED.date_created_source),
                    last_seen_at = EXCLUDED.last_seen_at,
                    is_active    = true,
                    deactivated_at = NULL,
                    district     = COALESCE(EXCLUDED.district,     re_realestate.listings.district),
                    municipality = COALESCE(EXCLUDED.municipality, re_realestate.listings.municipality),
                    auction_date           = COALESCE(EXCLUDED.auction_date,           re_realestate.listings.auction_date),
                    auction_starting_price = COALESCE(EXCLUDED.auction_starting_price, re_realestate.listings.auction_starting_price),
                    auction_deposit        = COALESCE(EXCLUDED.auction_deposit,        re_realestate.listings.auction_deposit),
                    -- Kontakt na makléře: scraper bez detailu (nebo zdroj, který ho neumí)
                    -- posílá NULL a nesmí tím smazat to, co už známe
                    seller_name    = COALESCE(EXCLUDED.seller_name,    re_realestate.listings.seller_name),
                    seller_email   = COALESCE(EXCLUDED.seller_email,   re_realestate.listings.seller_email),
                    seller_phone   = COALESCE(EXCLUDED.seller_phone,   re_realestate.listings.seller_phone),
                    seller_company = COALESCE(EXCLUDED.seller_company, re_realestate.listings.seller_company)
                RETURNING id
                """,
                listing_id,
                source_id,
                listing_data["source_code"],
                source_name,
                external_id,
                listing_data.get("url", ""),
                listing_data.get("title", "")[:200],
                listing_data.get("description", "")[:5000],
                property_type_db,
                offer_type_db,
                listing_data.get("price"),
                listing_data.get("location_text", "")[:200],
                listing_data.get("area_built_up"),
                listing_data.get("area_land"),
                listing_data.get("disposition"),
                listing_data.get("rooms"),
                listing_data.get("condition"),
                listing_data.get("construction_type"),
                listing_data.get("latitude"),
                listing_data.get("longitude"),
                now if listing_data.get("latitude") is not None else None,
                listing_data.get("geocode_source", "scraper") if listing_data.get("latitude") is not None else None,
                listing_data.get("view_count"),
                listing_data.get("date_created_source"),
                now,
                now,
                listing_data.get("district"),
                listing_data.get("municipality"),
                listing_data.get("auction_date"),
                listing_data.get("auction_starting_price"),
                listing_data.get("auction_deposit"),
                (listing_data.get("seller_name") or None) and listing_data["seller_name"][:200],
                (listing_data.get("seller_email") or None) and listing_data["seller_email"][:200],
                (listing_data.get("seller_phone") or None) and listing_data["seller_phone"][:100],
                (listing_data.get("seller_company") or None) and listing_data["seller_company"][:200],
                (listing_data.get("price_note") or None) and listing_data["price_note"][:200],
            )

            # Pokud UPDATE navrátil existující ID, použij to
            final_listing_id = result if result else listing_id

            # 🔥 Invalidace AI cenového signálu při změně jeho vstupů.
            # Signál se generuje z Kč/m², ale bulk job bere jen řádky s price_signal IS NULL,
            # takže bez tohohle by u inzerátu po zdražení nebo po opravě plochy natrvalo
            # zůstalo zdůvodnění počítané ze starých čísel.
            if old_row is not None:
                def _changed(old_val, new_val) -> bool:
                    if old_val is None and new_val is None:
                        return False
                    if old_val is None or new_val is None:
                        return True
                    return abs(float(old_val) - float(new_val)) > 0.5

                inputs_changed = (
                    _changed(old_row["price"], listing_data.get("price"))
                    or _changed(old_row["area_built_up"], listing_data.get("area_built_up"))
                    or _changed(old_row["area_land"], listing_data.get("area_land"))
                )
                if inputs_changed:
                    await conn.execute(
                        """
                        UPDATE re_realestate.listings
                        SET price_signal = NULL, price_signal_reason = NULL, price_signal_at = NULL
                        WHERE id = $1 AND price_signal IS NOT NULL
                        """,
                        final_listing_id,
                    )

            # 🔥 Zaloguj změnu ceny do price_history
            new_price = listing_data.get("price")
            if new_price is not None:
                # Log při první ceně (nový listing) NEBO při změně ceny
                if old_price is None or old_price != new_price:
                    await conn.execute(
                        """
                        INSERT INTO re_realestate.listing_price_history (listing_id, price, recorded_at, source)
                        VALUES ($1, $2, $3, 'scraper')
                        """,
                        final_listing_id, new_price, now
                    )
                    if old_price is not None and old_price != new_price:
                        diff_pct = round((float(new_price) - float(old_price)) / float(old_price) * 100, 1)
                        direction = "⬇" if new_price < old_price else "⬆"
                        logger.info(
                            f"Price change {direction} {direction}: {listing_data.get('source_code')} "
                            f"{external_id}: {old_price:,.0f} → {new_price:,.0f} Kč ({diff_pct:+.1f}%)"
                        )

            # Synchronizuj fotky v transakci
            if "photos" in listing_data and listing_data["photos"]:
                await self._upsert_photos(conn, final_listing_id, listing_data["photos"])
            
            logger.debug(f"Upserted listing {final_listing_id} (external_id={external_id})")
            return final_listing_id

    async def deactivate_listing(self, source_code: str, external_id: str) -> bool:
        """
        Okamžitě deaktivuje konkrétní inzerát (source_code + external_id).
        Používá se při detekci "Prodáno"/"Rezervováno" na detail stránce scraperu.

        Returns: True pokud byl inzerát nalezen a deaktivován, False pokud neexistoval.
        """
        async with self.acquire() as conn:
            status = await conn.execute(
                """
                UPDATE re_realestate.listings
                SET is_active = false,
                    deactivated_at = now()
                WHERE source_code = $1
                  AND external_id = $2
                  AND is_active = true
                """,
                source_code,
                external_id
            )
            affected = int(status.split()[-1]) if status else 0
            if affected > 0:
                logger.info(f"Deactivated sold/reserved listing: source={source_code} external_id={external_id}")
            return affected > 0

    async def count_unseen_listings(self, source_code: str, seen_since: datetime) -> int:
        """Kolik aktivních inzerátů by deactivate_unseen_listings deaktivoval (stejný WHERE)."""
        async with self.acquire() as conn:
            return await conn.fetchval(
                """
                SELECT count(*) FROM re_realestate.listings
                WHERE source_code = $1
                  AND is_active = true
                  AND last_seen_at < $2
                """,
                source_code,
                seen_since,
            )

    # Zdroje, jejichž nabídka je z našich okresů už výběrem nebo povahou (místní realitky,
    # hledání omezené na okresy). Jen u nich smí okres určit samotný název obce – u celostátních
    # zdrojů by „Nová Ves" odjinud dostala náš okres a prošla filtrem.
    _DISTRICT_BY_NAME_SOURCES = frozenset({
        "PREMIAREALITY", "NEMZNOJMO", "ZNOJMOREALITY", "HVREALITY", "DELUXREALITY",
        "REALMIX", "CENTURY21", "REMAX", "MMR",
    })
    _MUNICIPALITY_MAP_TTL_SECONDS = 6 * 3600

    async def _municipality_districts(self, conn) -> Dict[str, str]:
        """
        Slovník normalizovaný název obce → okres: úřední seznam obcí okresů Znojmo a Brno-venkov
        doplněný o obce a části obcí, které zná Sreality (okres tam dává portál sám). Jen
        jednoznačné názvy. Drží se v paměti 6 hodin.
        """
        from .district_lookup import normalize_place
        from .district_municipalities import official_municipality_districts

        cached = getattr(self, "_municipality_map_cache", None)
        if cached and time.monotonic() - cached[0] < self._MUNICIPALITY_MAP_TTL_SECONDS:
            return cached[1]

        rows = await conn.fetch(
            """
            SELECT municipality, min(district) AS district
            FROM re_realestate.listings
            WHERE source_code = 'SREALITY'
              AND municipality IS NOT NULL AND municipality <> ''
              AND district IS NOT NULL AND district <> ''
            GROUP BY municipality
            HAVING count(DISTINCT district) = 1 AND count(*) >= 2
            """
        )
        from_sreality: Dict[str, Optional[str]] = {}
        for row in rows:
            key = normalize_place(row["municipality"])
            # Stejný název po odstranění diakritiky ve dvou okresech = nejednoznačné
            from_sreality[key] = row["district"] if from_sreality.get(key, row["district"]) == row["district"] else None

        known = {k: v for k, v in from_sreality.items() if v}
        known.update(official_municipality_districts())   # úřední seznam má přednost
        self._municipality_map_cache = (time.monotonic(), known)
        return known

    async def _derive_district(self, listing_data: Dict[str, Any]) -> None:
        """
        Doplní listing_data["district"], když chybí: z GPS (polygon okresu), jinak u místních
        zdrojů z názvu obce. Chyba (tabulka okresů chybí, DB nedostupná) inzerát nezastaví.
        """
        if listing_data.get("district"):
            return
        from .district_lookup import district_from_place_names

        lat, lon = listing_data.get("latitude"), listing_data.get("longitude")
        by_name = listing_data.get("source_code") in self._DISTRICT_BY_NAME_SOURCES
        if (lat is None or lon is None) and not by_name:
            return
        try:
            async with self.acquire() as conn:
                if lat is not None and lon is not None \
                        and await conn.fetchval("SELECT to_regclass('re_realestate.districts') IS NOT NULL"):
                    district = await conn.fetchval(
                        """
                        SELECT name FROM re_realestate.districts
                        WHERE ST_Covers(geom, ST_SetSRID(ST_MakePoint($1, $2), 4326))
                        LIMIT 1
                        """,
                        float(lon), float(lat),
                    )
                    if district:
                        listing_data["district"] = district
                        return
                if by_name:
                    district = district_from_place_names(
                        listing_data.get("location_text"), listing_data.get("municipality"),
                        await self._municipality_districts(conn),
                    )
                    if district:
                        listing_data["district"] = district
        except Exception as exc:  # noqa: BLE001 – doplnění je best-effort
            logger.debug(f"District derivation failed: {exc}")

    async def get_known_prices(self, source_code: str) -> Dict[str, Optional[float]]:
        """external_id → cena všech inzerátů zdroje (i stažených). Pro scrapery, které detail
        stahují jen u nových inzerátů a při změně ceny (iDNES)."""
        async with self.acquire() as conn:
            rows = await conn.fetch(
                """
                SELECT external_id, price FROM re_realestate.listings
                WHERE source_code = $1 AND external_id IS NOT NULL
                """,
                source_code,
            )
        return {r["external_id"]: (float(r["price"]) if r["price"] is not None else None) for r in rows}

    async def touch_listings(
        self, source_code: str, items: Sequence[Tuple[str, Optional[str], Optional[str]]]
    ) -> int:
        """
        Označí známé inzeráty jako viděné (bez stahování detailu): last_seen_at, znovu aktivní.
        Položka = (external_id, obec, okres); obec a okres se doplní jen tam, kde chybí.

        Returns: počet položek, které se obnovovaly.
        """
        if not items:
            return 0
        now = datetime.utcnow()
        async with self.acquire() as conn:
            await conn.executemany(
                """
                UPDATE re_realestate.listings
                SET last_seen_at   = $5,
                    is_active      = true,
                    deactivated_at = NULL,
                    municipality   = COALESCE(NULLIF(municipality, ''), $3),
                    district       = COALESCE(NULLIF(district, ''), $4)
                WHERE source_code = $1 AND external_id = $2
                """,
                [(source_code, external_id, municipality, district, now) for external_id, municipality, district in items],
            )
        return len(items)

    async def mark_active_seen(self, source_code: str) -> int:
        """
        Všechny aktivní inzeráty zdroje označí jako právě viděné. Pro běh, který nestihl projít
        celý výpis: plný rescan by jinak deaktivoval i inzeráty, ke kterým se scraper nedostal.
        """
        async with self.acquire() as conn:
            status = await conn.execute(
                "UPDATE re_realestate.listings SET last_seen_at = $2 WHERE source_code = $1 AND is_active",
                source_code, datetime.utcnow(),
            )
        return int(status.split()[-1])

    _SCRAPE_SKIPS_DDL = """
        CREATE TABLE IF NOT EXISTS re_realestate.scrape_skips (
            source_code      text NOT NULL,
            external_id      text NOT NULL,
            reason           text,
            first_skipped_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (source_code, external_id)
        )
    """
    _SCRAPE_SKIPS_KEEP_DAYS = 60

    async def get_skipped_ids(self, source_code: str) -> set:
        """
        Inzeráty zdroje, u kterých scraper po stažení detailu zjistil, že je neukládá (Realingo:
        původní inzerát je z portálu, který bereme přímo). Pamatujeme si je, aby se jejich detail
        nestahoval každou noc znovu; po 60 dnech se záznam zapomene a ověří znovu.
        """
        async with self.acquire() as conn:
            await conn.execute(self._SCRAPE_SKIPS_DDL)
            await conn.execute(
                "DELETE FROM re_realestate.scrape_skips WHERE first_skipped_at < now() - make_interval(days => $1)",
                self._SCRAPE_SKIPS_KEEP_DAYS,
            )
            rows = await conn.fetch(
                "SELECT external_id FROM re_realestate.scrape_skips WHERE source_code = $1", source_code)
        return {r["external_id"] for r in rows}

    async def remember_skipped(self, source_code: str, external_ids: Sequence[str], reason: str) -> None:
        if not external_ids:
            return
        async with self.acquire() as conn:
            await conn.execute(self._SCRAPE_SKIPS_DDL)
            await conn.executemany(
                """
                INSERT INTO re_realestate.scrape_skips (source_code, external_id, reason)
                VALUES ($1, $2, $3)
                ON CONFLICT (source_code, external_id) DO NOTHING
                """,
                [(source_code, external_id, reason) for external_id in external_ids],
            )

    async def fill_missing_districts(self) -> Tuple[int, int]:
        """
        Doplní listings.district tam, kde ho zdroj nedal (Reas, Prodejme.to, část iDNES).

        1) Z GPS: bod inzerátu leží v polygonu okresu (tabulka re_realestate.districts,
           scripts/migrate_districts.sql). Proti Sreality sedí v 4 494 z 4 496 případů.
        2) Bez GPS: z názvu obce přes slovník obec → okres ze Sreality (jen jednoznačné obce).

        Returns: (doplněno z GPS, doplněno z názvu obce)
        """
        from .district_lookup import district_from_place_names, normalize_place

        async with self.acquire() as conn:
            from_gps = 0
            if await conn.fetchval("SELECT to_regclass('re_realestate.districts') IS NOT NULL"):
                status = await conn.execute(
                    """
                    UPDATE re_realestate.listings l
                    SET district = d.name
                    FROM re_realestate.districts d
                    WHERE (l.district IS NULL OR l.district = '')
                      AND l.location_point IS NOT NULL
                      AND ST_Covers(d.geom, l.location_point)
                    """
                )
                from_gps = int(status.split()[-1])

            missing = await conn.fetch(
                """
                SELECT id, location_text, municipality
                FROM re_realestate.listings
                WHERE is_active AND (district IS NULL OR district = '')
                """
            )
            if not missing:
                return from_gps, 0

            unique = await self._municipality_districts(conn)

            updates = []
            for row in missing:
                district = district_from_place_names(row["location_text"], row["municipality"], unique)
                if district:
                    updates.append((district, row["id"]))
            if updates:
                await conn.executemany(
                    "UPDATE re_realestate.listings SET district = $1 WHERE id = $2 AND (district IS NULL OR district = '')",
                    updates,
                )
            return from_gps, len(updates)

    async def deactivate_unseen_listings(self, source_code: str, seen_since: datetime) -> int:
        """
        Deaktivuje inzeráty ze zdroje source_code, které nebyly viděny od seen_since.
        Volá se po full_rescan – inzeráty které scraper nevrátil jsou expirované.

        Returns: počet deaktivovaných inzerátů
        """
        async with self.acquire() as conn:
            status = await conn.execute(
                """
                UPDATE re_realestate.listings
                SET is_active = false,
                    deactivated_at = now()
                WHERE source_code = $1
                  AND is_active = true
                  AND last_seen_at < $2
                """,
                source_code,
                seen_since
            )
            # asyncpg vrací "UPDATE N" – parsujeme počet ovlivněných řádků
            deactivated = int(status.split()[-1]) if status else 0
            if deactivated > 0:
                logger.info(f"Deactivated {deactivated} expired listings for source {source_code} (not seen since {seen_since})")
            return deactivated

    async def _download_photo_to_storage(
        self,
        photo_url: str,
        listing_id: UUID,
        http_client: httpx.AsyncClient,
    ) -> Optional[str]:
        """
        Stáhne fotku z CDN a uloží ji do sdíleného uploads volume.
        Vrátí relativní stored_url (např. "/uploads/listings/{id}/photos/3f2a9c01d4e5b6a7.jpg")
        nebo None pokud download selhal.

        Název souboru = hash original_url. Dřív to byl order_index, jenže nová fotka
        vložená na už obsazenou pozici přepsala soubor jiné fotky (sdílený stored_url).

        Cesta v kontejneru: {UPLOADS_BASE_PATH}/listings/{id}/photos/{hash}.ext
        API volume mountpoint:  /app/wwwroot/uploads  → stored_url prefix /uploads/
        Scraper volume mountpoint: /app/uploads      → stored_url prefix /uploads/
        """
        uploads_base = _get_uploads_base_path()
        if uploads_base is None:
            return None  # Inline download není nakonfigurován

        try:
            response = await http_client.get(photo_url, headers=_PHOTO_DOWNLOAD_HEADERS, follow_redirects=True)
            if response.status_code != 200:
                logger.debug(f"Photo download HTTP {response.status_code}: {photo_url}")
                return None

            content_type = response.headers.get("content-type", "image/jpeg").split(";")[0].strip()
            ext = {
                "image/png": ".png",
                "image/webp": ".webp",
                "image/gif": ".gif",
            }.get(content_type, ".jpg")

            # Uložit do sdíleného volume
            photo_dir = uploads_base / "listings" / str(listing_id) / "photos"
            photo_dir.mkdir(parents=True, exist_ok=True)
            file_name = f"{photo_file_stem(photo_url)}{ext}"
            (photo_dir / file_name).write_bytes(response.content)

            return f"/uploads/listings/{listing_id}/photos/{file_name}"

        except Exception as e:
            logger.debug(f"Photo download failed for {photo_url}: {e}")
            return None

    async def _upsert_photos(self, conn, listing_id: UUID, photo_urls: List[str]) -> None:
        """
        Upsert fotek pro listing. Nové fotky jsou ihned staženy inline (pokud je
        UPLOADS_BASE_PATH nastaven) — kritické pro CDN s krátkodobými tokeny (Sreality sdn.cz).

        ⚠️  ZACHOVÁVÁ KLASIFIKACE jen u fotek se stejnou URL:
        - Existující fotky s classified_at != NULL jsou ponechány (včetně metadata)
        - Jen updatuje order_index, pokud se změnil
        - Smaže jen fotky, které už nejsou v novém seznamu
        - Přidá nové fotky, pokud se objevily – vždy neklasifikované; klasifikace se nikdy
          nepřenáší podle pořadí (viz _gallery_needs_reclassification)

        WICHTIG: Běží v transakci, aby byly operace atomické.
        """
        # Pre-download nových fotek PŘED transakcí (HTTP I/O mimo transakci)
        new_urls_to_download: Dict[str, Optional[str]] = {}  # url → stored_url or None
        uploads_base = _get_uploads_base_path()

        # Zjistí, které URLs jsou NOVÉ (ještě nejsou v DB)
        existing_check = await conn.fetch(
            "SELECT original_url, stored_url FROM re_realestate.listing_photos WHERE listing_id = $1",
            listing_id,
        )
        existing_urls_set = {row["original_url"] for row in existing_check}
        # Fotky které jsou v DB ale nemají stored_url (download dříve selhal) → retry
        existing_no_stored = {row["original_url"] for row in existing_check if row["stored_url"] is None}

        new_photo_urls = [
            (idx, url) for idx, url in enumerate(photo_urls[:50])
            if url not in existing_urls_set
        ]
        retry_photo_urls = [
            (idx, url) for idx, url in enumerate(photo_urls[:50])
            if url in existing_no_stored
        ]
        all_download_urls = new_photo_urls + retry_photo_urls

        if all_download_urls and uploads_base is not None:
            async with httpx.AsyncClient(timeout=15.0) as http_client:
                for _, url in all_download_urls:
                    stored = await self._download_photo_to_storage(url, listing_id, http_client)
                    new_urls_to_download[url] = stored
            downloaded = sum(1 for v in new_urls_to_download.values() if v is not None)
            if all_download_urls:
                logger.debug(
                    f"Inline photo download: {downloaded}/{len(all_download_urls)} OK "
                    f"({len(new_photo_urls)} new, {len(retry_photo_urls)} retry) for listing {listing_id}"
                )

        async with conn.transaction():
            # Načíst existující fotky pro tento listing
            existing = await conn.fetch(
                """
                SELECT id, original_url, order_index, classified_at, stored_url,
                       photo_category, photo_labels, damage_detected,
                       classification_confidence, photo_description
                FROM re_realestate.listing_photos
                WHERE listing_id = $1
                """,
                listing_id,
            )

            existing_by_url = {row["original_url"]: row for row in existing}
            new_urls_set = set(photo_urls[:50])

            # 1. UPDATE existujících fotek (změněný order_index nebo retry stored_url)
            for idx, photo_url in enumerate(photo_urls[:50]):
                if photo_url in existing_by_url:
                    row = existing_by_url[photo_url]
                    new_order = row["order_index"] != idx
                    retry_stored = new_urls_to_download.get(photo_url) if photo_url in existing_no_stored else None
                    if new_order and retry_stored:
                        await conn.execute(
                            "UPDATE re_realestate.listing_photos SET order_index = $1, stored_url = $2 WHERE id = $3",
                            idx, retry_stored, row["id"],
                        )
                    elif new_order:
                        await conn.execute(
                            "UPDATE re_realestate.listing_photos SET order_index = $1 WHERE id = $2",
                            idx, row["id"],
                        )
                    elif retry_stored:
                        await conn.execute(
                            "UPDATE re_realestate.listing_photos SET stored_url = $1 WHERE id = $2",
                            retry_stored, row["id"],
                        )

            # 2. INSERT nových fotek (které ještě nejsou v DB)
            for idx, photo_url in enumerate(photo_urls[:50]):
                if photo_url not in existing_by_url:
                    photo_id = uuid4()
                    stored_url = new_urls_to_download.get(photo_url)  # None pokud download selhal
                    await conn.execute(
                        """
                        INSERT INTO re_realestate.listing_photos (
                            id, listing_id, original_url, order_index, stored_url, created_at
                        )
                        VALUES ($1, $2, $3, $4, $5, $6)
                        """,
                        photo_id,
                        listing_id,
                        photo_url,
                        idx,
                        stored_url,
                        datetime.utcnow(),
                    )

            # 3. DELETE fotek, které zmizely. Klasifikovanou fotku necháme jen tehdy, když máme
            #    uloženou kopii (jinak není co zobrazit – původní URL už neexistuje a v UI by byla černá).
            urls_to_delete = set(existing_by_url.keys()) - new_urls_set
            for url in urls_to_delete:
                row = existing_by_url[url]
                if row["classified_at"] is None or row["stored_url"] is None:
                    await conn.execute(
                        "DELETE FROM re_realestate.listing_photos WHERE id = $1",
                        row["id"],
                    )

        if _gallery_needs_reclassification(existing, photo_urls[:50]):
            self._galleries_to_reclassify.add(listing_id)

        logger.debug(f"Upserted {len(photo_urls)} photos for listing {listing_id} (preserved classifications)")

    def pop_galleries_to_reclassify(self) -> List[UUID]:
        """Vrátí a vyprázdní seznam inzerátů, jejichž klasifikovaná galerie od posledního volání dostala nové fotky."""
        listing_ids = sorted(self._galleries_to_reclassify, key=str)
        self._galleries_to_reclassify.clear()
        return listing_ids

    
    # ============================================================================
    # Scrape Jobs Persistence
    # ============================================================================
    
    async def create_scrape_job(self, job_id: UUID, source_codes: List[str], 
                                full_rescan: bool = False) -> None:
        """
        Vytvoří záznam scrape jobu v databázi.
        
        Args:
            job_id: UUID jobu
            source_codes: List zdrojů k scrapování
            full_rescan: true pro full rescan, false pro incrementální
        """
        async with self.acquire() as conn:
            await conn.execute(
                """
                INSERT INTO re_realestate.scrape_jobs (
                    id, source_codes, full_rescan, status, progress, created_at
                )
                VALUES ($1, $2, $3, $4, $5, $6)
                """,
                job_id,
                source_codes,  # asyncpg automaticky konvertuje list na PostgreSQL array
                full_rescan,
                'Queued',
                0,
                datetime.utcnow()
            )
            logger.info(f"Created scrape job {job_id} for sources: {source_codes}")
    
    async def update_scrape_job(self, job_id: UUID, status: str, 
                               progress: int = None, error_message: str = None,
                               listings_found: int = None, listings_new: int = None,
                               listings_updated: int = None, started_at: datetime = None,
                               finished_at: datetime = None) -> None:
        """
        Aktualizuje scrape job s novými daty.
        
        Args:
            job_id: UUID jobu
            status: Nový status
            progress: Progress 0-100
            error_message: Chybová zpráva
            listings_found: Počet nalezených inzerátů
            listings_new: Počet nových inzerátů
            listings_updated: Počet aktualizovaných inzerátů
            started_at: Čas startu jobu
            finished_at: Čas ukončení jobu
        """
        updates = []
        params = []
        param_idx = 1
        
        # Build dynamic UPDATE query
        if status is not None:
            updates.append(f"status = ${param_idx}")
            params.append(status)
            param_idx += 1
        
        if progress is not None:
            updates.append(f"progress = ${param_idx}")
            params.append(progress)
            param_idx += 1
        
        if error_message is not None:
            updates.append(f"error_message = ${param_idx}")
            params.append(error_message)
            param_idx += 1
        
        if listings_found is not None:
            updates.append(f"listings_found = ${param_idx}")
            params.append(listings_found)
            param_idx += 1
        
        if listings_new is not None:
            updates.append(f"listings_new = ${param_idx}")
            params.append(listings_new)
            param_idx += 1
        
        if listings_updated is not None:
            updates.append(f"listings_updated = ${param_idx}")
            params.append(listings_updated)
            param_idx += 1
        
        if started_at is not None:
            updates.append(f"started_at = ${param_idx}")
            params.append(started_at)
            param_idx += 1
        
        if finished_at is not None:
            updates.append(f"finished_at = ${param_idx}")
            params.append(finished_at)
            param_idx += 1
        
        if not updates:
            return
        
        # Přidej job_id jako poslední parametr
        params.append(job_id)
        
        query = f"""
            UPDATE re_realestate.scrape_jobs
            SET {', '.join(updates)}
            WHERE id = ${param_idx}
        """
        
        async with self.acquire() as conn:
            await conn.execute(query, *params)
            logger.debug(f"Updated scrape job {job_id}: {', '.join(updates)}")
    
    async def get_scrape_job(self, job_id: UUID) -> Optional[Dict[str, Any]]:
        """
        Načte scrape job z databáze.
        
        Args:
            job_id: UUID jobu
            
        Returns:
            Dict se scrape job daty nebo None
        """
        async with self.acquire() as conn:
            row = await conn.fetchrow(
                """
                SELECT id, source_codes, full_rescan, status, progress,
                       listings_found, listings_new, listings_updated,
                       error_message, created_at, started_at, finished_at
                FROM re_realestate.scrape_jobs
                WHERE id = $1
                """,
                job_id
            )
            if row:
                return dict(row)
            return None
    
    async def list_scrape_jobs(self, limit: int = 50, status: str = None) -> List[Dict[str, Any]]:
        """
        Vypíše scrape joby seřazené chronologicky (nejnovější první).
        
        Args:
            limit: Maximální počet jobů
            status: Filtr podle statusu (např. "Queued", "Running", "Succeeded")
            
        Returns:
            List scrape jobů
        """
        query = """
            SELECT id, source_codes, full_rescan, status, progress,
                   listings_found, listings_new, listings_updated,
                   error_message, created_at, started_at, finished_at
            FROM re_realestate.scrape_jobs
        """
        
        if status:
            query += f" WHERE status = '{status}'"
        
        query += " ORDER BY created_at DESC LIMIT $1"
        
        async with self.acquire() as conn:
            rows = await conn.fetch(query, limit)
            return [dict(row) for row in rows]


# Globální instance (singleton pattern)
_db_manager: Optional[DatabaseManager] = None


def get_db_manager() -> DatabaseManager:
    """Získá globální instanci DatabaseManager."""
    global _db_manager
    if _db_manager is None:
        raise RuntimeError("DatabaseManager not initialized. Call init_db_manager() first.")
    return _db_manager


def init_db_manager(host: str, port: int, database: str, user: str, password: str,
                   min_size: int = 5, max_size: int = 20, 
                   source_cache_ttl_seconds: int = 3600) -> DatabaseManager:
    """
    Inicializuje globální DatabaseManager.
    
    Args:
        host: Database host
        port: Database port
        database: Database name
        user: Database user
        password: Database password
        min_size: Min connection pool size
        max_size: Max connection pool size
        source_cache_ttl_seconds: Time-to-live pro in-memory source code cache (default 1 hour)
    """
    global _db_manager
    _db_manager = DatabaseManager(
        host, port, database, user, password, 
        min_size, max_size, source_cache_ttl_seconds
    )
    return _db_manager
