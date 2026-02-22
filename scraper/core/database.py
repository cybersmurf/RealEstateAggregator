"""
Database utilities for scraper.
Provides async connection pool and CRUD operations for listings.
"""
import asyncpg
import logging
from typing import Optional, Dict, Any, List
from uuid import UUID, uuid4
from datetime import datetime
from contextlib import asynccontextmanager

logger = logging.getLogger(__name__)


class DatabaseManager:
    """Manages PostgreSQL connection pool for scraper."""
    
    def __init__(self, host: str, port: int, database: str, user: str, password: str, 
                 min_size: int = 5, max_size: int = 20):
        self.host = host
        self.port = port
        self.database = database
        self.user = user
        self.password = password
        self.min_size = min_size
        self.max_size = max_size
        self._pool: Optional[asyncpg.Pool] = None
    
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
    
    @asynccontextmanager
    async def acquire(self):
        """Acquire connection from pool."""
        if self._pool is None:
            raise RuntimeError("Database pool not initialized. Call connect() first.")
        
        async with self._pool.acquire() as conn:
            yield conn
    
    async def get_source_by_code(self, source_code: str) -> Optional[Dict[str, Any]]:
        """
        Získá source (zdroj) podle kódu.
        
        Args:
            source_code: Kód zdroje (např. "REMAX", "MMR")
            
        Returns:
            Dict se source daty nebo None
        """
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
                return dict(row)
            return None
    
    async def upsert_listing(self, listing_data: Dict[str, Any]) -> UUID:
        """
        Upsert listing do databáze.
        
        Pokud listing s daným external_id a source_code již existuje, aktualizuje ho.
        Pokud neexistuje, vytvoří nový.
        
        Args:
            listing_data: Dictionary s daty listingu
            
        Returns:
            UUID listingu (nového nebo existujícího)
        """
        # Získej source_id podle source_code
        source = await self.get_source_by_code(listing_data["source_code"])
        if not source:
            raise ValueError(f"Source '{listing_data['source_code']}' not found in database")
        
        source_id = source["id"]
        source_name = source["name"]
        external_id = listing_data.get("external_id")
        
        # Mapování českých hodnot na enum hodnoty v DB
        property_type_map = {
            "Dům": "House",
            "Byt": "Apartment",
            "Pozemek": "Land",
            "Chata": "Cottage",
            "Komerční": "Commercial",
            "Průmyslový": "Industrial",
            "Garáž": "Garage",
            "Ostatní": "Other",
        }
        
        offer_type_map = {
            "Prodej": "Sale",
            "Pronájem": "Rent",
        }
        
        property_type_db = property_type_map.get(listing_data.get("property_type", "Ostatní"), "Other")
        offer_type_db = offer_type_map.get(listing_data.get("offer_type", "Prodej"), "Sale")
        
        async with self.acquire() as conn:
            # Zkontroluj jestli listing existuje
            existing = await conn.fetchrow(
                """
                SELECT id FROM re_realestate.listings
                WHERE source_id = $1 AND external_id = $2
                """,
                source_id,
                external_id
            )
            
            now = datetime.utcnow()
            
            if existing:
                # UPDATE
                listing_id = existing["id"]
                await conn.execute(
                    """
                    UPDATE re_realestate.listings
                    SET 
                        url = $1,
                        title = $2,
                        description = $3,
                        property_type = $4,
                        offer_type = $5,
                        price = $6,
                        location_text = $7,
                        area_built_up = $8,
                        area_land = $9,
                        last_seen_at = $10,
                        is_active = true
                    WHERE id = $11
                    """,
                    listing_data.get("url", ""),
                    listing_data.get("title", "")[:200],
                    listing_data.get("description", "")[:5000],
                    property_type_db,
                    offer_type_db,
                    listing_data.get("price"),
                    listing_data.get("location_text", "")[:200],
                    listing_data.get("area_built_up"),
                    listing_data.get("area_land"),
                    now,
                    listing_id
                )
                logger.debug(f"Updated listing {listing_id} (external_id={external_id})")
            else:
                # INSERT
                listing_id = uuid4()
                await conn.execute(
                    """
                    INSERT INTO re_realestate.listings (
                        id, source_id, source_code, source_name, external_id, url,
                        title, description, property_type, offer_type, price,
                        location_text, area_built_up, area_land,
                        first_seen_at, last_seen_at, is_active
                    )
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, true)
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
                    now,
                    now
                )
                logger.debug(f"Inserted new listing {listing_id} (external_id={external_id})")
            
            # Synchronizuj fotky
            if "photos" in listing_data and listing_data["photos"]:
                await self._upsert_photos(conn, listing_id, listing_data["photos"])
            
            return listing_id
    
    async def _upsert_photos(self, conn, listing_id: UUID, photo_urls: List[str]) -> None:
        """
        Upsert fotek pro listing.
        
        Smaže staré fotky a vloží nové.
        """
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
                    id, listing_id, original_url, "order", created_at
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


# Globální instance (singleton pattern)
_db_manager: Optional[DatabaseManager] = None


def get_db_manager() -> DatabaseManager:
    """Získá globální instanci DatabaseManager."""
    global _db_manager
    if _db_manager is None:
        raise RuntimeError("DatabaseManager not initialized. Call init_db_manager() first.")
    return _db_manager


def init_db_manager(host: str, port: int, database: str, user: str, password: str,
                   min_size: int = 5, max_size: int = 20) -> DatabaseManager:
    """Inicializuje globální DatabaseManager."""
    global _db_manager
    _db_manager = DatabaseManager(host, port, database, user, password, min_size, max_size)
    return _db_manager
