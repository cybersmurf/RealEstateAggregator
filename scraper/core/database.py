"""
Database utilities for scraper.
Provides async connection pool and CRUD operations for listings.
"""
import asyncpg
import logging
from typing import Optional, Dict, Any, List
from uuid import UUID, uuid4
from datetime import datetime, timedelta
from contextlib import asynccontextmanager

from .filters import get_filter_manager

logger = logging.getLogger(__name__)


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
            # 🔥 ATOMIC UPSERT s ON CONFLICT DO UPDATE
            # Žádné race conditions - DB se postará o atomicitu
            result = await conn.fetchval(
                """
                INSERT INTO re_realestate.listings (
                    id, source_id, source_code, source_name, external_id, url,
                    title, description, property_type, offer_type, price,
                    location_text, area_built_up, area_land, disposition,
                    latitude, longitude, geocoded_at, geocode_source,
                    first_seen_at, last_seen_at, is_active
                )
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, true)
                ON CONFLICT (source_id, external_id) DO UPDATE
                SET
                    url = EXCLUDED.url,
                    title = EXCLUDED.title,
                    description = EXCLUDED.description,
                    property_type = EXCLUDED.property_type,
                    offer_type = EXCLUDED.offer_type,
                    price = EXCLUDED.price,
                    location_text = EXCLUDED.location_text,
                    area_built_up = EXCLUDED.area_built_up,
                    area_land = EXCLUDED.area_land,
                    disposition = EXCLUDED.disposition,
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
                    last_seen_at = EXCLUDED.last_seen_at,
                    is_active = true
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
                listing_data.get("latitude"),
                listing_data.get("longitude"),
                now if listing_data.get("latitude") is not None else None,
                listing_data.get("geocode_source", "scraper") if listing_data.get("latitude") is not None else None,
                now,
                now
            )
            
            # Pokud UPDATE navrátil existující ID, použij to
            final_listing_id = result if result else listing_id
            
            # Synchronizuj fotky v transakci
            if "photos" in listing_data and listing_data["photos"]:
                await self._upsert_photos(conn, final_listing_id, listing_data["photos"])
            
            logger.debug(f"Upserted listing {final_listing_id} (external_id={external_id})")
            return final_listing_id

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
                SET is_active = false
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

    async def _upsert_photos(self, conn, listing_id: UUID, photo_urls: List[str]) -> None:
        """
        Upsert fotek pro listing.
        
        Smaže staré fotky a vloží nové.
        WICHTIG: Běží v transakci, aby DELETE+INSERT byly atomické.
        """
        async with conn.transaction():
            # Smazat staré fotky
            await conn.execute(
                "DELETE FROM re_realestate.listing_photos WHERE listing_id = $1",
                listing_id
            )
            
            # Vložit nové fotky
            for idx, photo_url in enumerate(photo_urls[:20]):  # Max 20 photos
                photo_id = uuid4()
                await conn.execute(
                    """
                    INSERT INTO re_realestate.listing_photos (
                        id, listing_id, original_url, order_index, created_at
                    )
                    VALUES ($1, $2, $3, $4, $5)
                    """,
                    photo_id,
                    listing_id,
                    photo_url,
                    idx,
                    datetime.utcnow()
                )
        
        logger.debug(f"Upserted {len(photo_urls)} photos for listing {listing_id}")
    
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
