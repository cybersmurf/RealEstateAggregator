"""
Geocoding utilities pro Real Estate Aggregator.
Používá Nominatim (OpenStreetMap) – zdarma, bez API klíče.

Nominatim ToS: max 1 request/sec, User-Agent povinný.
Pro produkci doporučujeme vlastní Nominatim instance.
"""
import asyncio
import logging
from typing import Optional, Tuple
import httpx

logger = logging.getLogger(__name__)

NOMINATIM_URL = "https://nominatim.openstreetmap.org/search"
USER_AGENT = "RealEstateAggregator/1.0 (https://github.com/cybersmurf/RealEstateAggregator)"

# Throttle: max 1 req/s dle Nominatim ToS
_RATE_LIMIT_DELAY = 1.1  # sekund mezi requesty


async def geocode_address(address: str, country: str = "CZ") -> Optional[Tuple[float, float]]:
    """
    Geokóduje adresu pomocí Nominatim (OpenStreetMap).
    
    Args:
        address: Adresa jako string (např. "Znojmo, Jihomoravský kraj")
        country:  ISO 3166-1 alpha-2 kód země (default "CZ")
        
    Returns:
        Tuple (latitude, longitude) nebo None pokud nebylo nalezeno.
    """
    params = {
        "q": address,
        "countrycodes": country.lower(),
        "format": "json",
        "limit": 1,
        "accept-language": "cs",
    }

    headers = {"User-Agent": USER_AGENT}

    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.get(NOMINATIM_URL, params=params, headers=headers)
            response.raise_for_status()
            results = response.json()

        if results:
            lat = float(results[0]["lat"])
            lon = float(results[0]["lon"])
            logger.debug(f"Geocoded '{address}' → ({lat}, {lon})")
            return lat, lon

        logger.debug(f"Geocoding nenalezen pro: '{address}'")
        return None

    except Exception as exc:
        logger.warning(f"Geocoding selhal pro '{address}': {exc}")
        return None


async def geocode_listing_location(location_text: str,
                                    municipality: Optional[str] = None,
                                    district: Optional[str] = None) -> Optional[Tuple[float, float]]:
    """
    Geokóduje lokaci inzerátu. Zkouší od nejpřesnější po nejméně přesnou.
    
    Pořadí pokusů:
    1. Celý location_text (nejpřesnější)
    2. municipality + district
    3. Samotný district
    """
    attempts = [location_text]

    if municipality and district:
        attempts.append(f"{municipality}, {district}, Česká republika")

    if district:
        attempts.append(f"{district}, Česká republika")

    for attempt in attempts:
        if not attempt or len(attempt.strip()) < 3:
            continue

        result = await geocode_address(attempt)
        if result:
            return result

        # Rate limit mezi pokusy
        await asyncio.sleep(_RATE_LIMIT_DELAY)

    return None


async def bulk_geocode(db_manager, batch_size: int = 50) -> int:
    """
    Dávkové geokódování inzerátů bez souřadnic.
    
    Args:
        db_manager:  Instance DatabaseManager
        batch_size:  Počet inzerátů v jedné dávce (default 50)
        
    Returns:
        Počet úspěšně geokódovaných inzerátů.
    """
    from datetime import datetime

    logger.info(f"Spouštím bulk geocoding (batch_size={batch_size})")
    success_count = 0

    async with db_manager.acquire() as conn:
        # Načti inzeráty bez souřadnic
        rows = await conn.fetch(
            """
            SELECT id, location_text, municipality, district
            FROM re_realestate.listings
            WHERE is_active = true
              AND latitude IS NULL
            ORDER BY first_seen_at DESC
            LIMIT $1
            """,
            batch_size
        )

    if not rows:
        logger.info("Žádné inzeráty bez souřadnic – geocoding přeskočen")
        return 0

    logger.info(f"Geokóduji {len(rows)} inzerátů...")

    for row in rows:
        coords = await geocode_listing_location(
            location_text=row["location_text"],
            municipality=row["municipality"],
            district=row["district"]
        )

        if coords:
            lat, lon = coords
            async with db_manager.acquire() as conn:
                await conn.execute(
                    """
                    UPDATE re_realestate.listings
                    SET latitude = $1,
                        longitude = $2,
                        geocoded_at = $3,
                        geocode_source = 'nominatim'
                    WHERE id = $4
                    """,
                    lat, lon, datetime.utcnow(), row["id"]
                )
            success_count += 1
            logger.debug(f"Geokódován inzerát {row['id']}: ({lat}, {lon})")

        # Nominatim rate limit: max 1 req/s
        await asyncio.sleep(_RATE_LIMIT_DELAY)

    logger.info(f"Bulk geocoding dokončen: {success_count}/{len(rows)} úspěšně")
    return success_count
