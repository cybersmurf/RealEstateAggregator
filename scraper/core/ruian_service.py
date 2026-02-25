"""
RUIAN (Registr územní identifikace, adres a nemovitostí) API integration.

Používá veřejné ČÚZK ArcGIS REST API k nalezení kódu adresního místa dle
adresy. Z něj pak sestaví přímý link na Nahlížení do katastru nemovitostí.

API dokumentace: https://ags.cuzk.cz/arcgis/rest/services/RUIAN/
Rate limit: není explicitní, doporučuje se max 1 req/s.
"""

import asyncio
import logging
import re
from typing import Optional
import httpx

logger = logging.getLogger(__name__)

RUIAN_FIND_URL = (
    "https://ags.cuzk.cz/arcgis/rest/services/RUIAN/"
    "Vyhledavaci_sluzba_nad_daty_RUIAN/MapServer/find"
)
NAHLIZENIDOKN_BASE = "https://nahlizenidokn.cuzk.cz"


def build_cadastre_url(ruian_kod: Optional[int]) -> str:
    """Sestaví přímý odkaz na nahlížení.cuzk.cz pro dané adresní místo."""
    if ruian_kod:
        return (
            f"{NAHLIZENIDOKN_BASE}/ZobrazitMapu/Basic"
            f"?typeCode=adresniMisto&id={ruian_kod}"
        )
    return f"{NAHLIZENIDOKN_BASE}/"


async def lookup_ruian_address(
    address_text: str,
    municipality: Optional[str] = None,
) -> dict:
    """
    Vyhledá adresu v RUIAN a vrátí kód adresního místa.

    Args:
        address_text: Celý text adresy z inzerátu (např. "Kravsko 100, okres Znojmo")
        municipality:  Název obce (přesnější výsledky)

    Returns:
        dict s klíči:
          ruian_kod       – int nebo None
          cadastre_url    – přímý link na nahlížení
          address_used    – adresa skutečně použitá pro vyhledávání
          fetch_status    – "found" | "not_found" | "error"
          raw_ruian       – surová odpověď RUIAN (dict)
          parcel_number   – None (dostupné jen přes placené API)
    """
    # Preferuj obec jako vstup (kratší, přesnější)
    search_text = municipality.strip() if municipality else address_text.strip()
    # Odstraň přebytečné části jako "okres XY", "kraj XY"
    search_text = re.sub(r",?\s*(okres|kraj|okr\.)\s+\S+", "", search_text).strip()
    # Zkrať na max 100 znaků
    search_text = search_text[:100]

    params = {
        "searchText": search_text,
        "contains": "true",
        "layers": "2",          # Layer 2 = Adresní místa v RUIAN MapServeru
        "returnGeometry": "false",
        "f": "json",
    }

    result = {
        "ruian_kod": None,
        "cadastre_url": build_cadastre_url(None),
        "address_used": search_text,
        "fetch_status": "not_found",
        "raw_ruian": None,
        "parcel_number": None,
    }

    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.get(
                RUIAN_FIND_URL,
                params=params,
                headers={"User-Agent": "RealEstateAggregator/1.0 (educational project)"},
            )
            response.raise_for_status()
            data = response.json()
            result["raw_ruian"] = data

        results_list = data.get("results", [])
        if not results_list:
            logger.debug("RUIAN lookup '%s' → žádné výsledky", search_text)
            return result

        first = results_list[0]
        attributes = first.get("attributes", {})

        # Kód adresního místa může být pod různými klíči dle verze RUIAN MapServeru
        kod = (
            attributes.get("KOD")
            or attributes.get("kod")
            or attributes.get("KOD_ADM")
            or attributes.get("OBJECTID")
        )

        if kod:
            result["ruian_kod"] = int(kod)
            result["cadastre_url"] = build_cadastre_url(int(kod))
            result["fetch_status"] = "found"
            logger.info("RUIAN lookup '%s' → kód %s", search_text, kod)
        else:
            logger.debug("RUIAN lookup '%s' → výsledek bez kódu: %s", search_text, attributes)

    except httpx.HTTPError as e:
        logger.warning("RUIAN HTTP chyba pro '%s': %s", search_text, e)
        result["fetch_status"] = "error"
        result["raw_ruian"] = {"error": str(e)}
    except Exception as e:
        logger.exception("RUIAN neočekávaná chyba pro '%s': %s", search_text, e)
        result["fetch_status"] = "error"
        result["raw_ruian"] = {"error": str(e)}

    return result


async def bulk_ruian_lookup(
    db_manager,
    batch_size: int = 50,
    overwrite_not_found: bool = False,
) -> dict:
    """
    Hromadné vyhledávání RUIAN pro inzeráty bez katastrálních dat.

    Args:
        db_manager:           instance DatabaseManager
        batch_size:           max počet inzerátů v jedné dávce
        overwrite_not_found:  true = přepíše i záznamy se stavem 'not_found'

    Returns:
        dict s počty: {total, found, not_found, error, skipped}
    """
    stats = {"total": 0, "found": 0, "not_found": 0, "error": 0, "skipped": 0}

    # Načti seznam inzerátů přes pool
    if overwrite_not_found:
        status_filter = "('pending', 'not_found', 'error')"
    else:
        status_filter = "('pending', 'error')"

    async with db_manager.acquire() as conn:
        rows = await conn.fetch(
            f"""
            SELECT l.id, l.location_text, l.municipality, l.district
            FROM re_realestate.listings l
            WHERE l.is_active = true
              AND NOT EXISTS (
                  SELECT 1 FROM re_realestate.listing_cadastre_data lcd
                  WHERE lcd.listing_id = l.id
                    AND lcd.fetch_status NOT IN {status_filter}
              )
            LIMIT $1
            """,
            batch_size,
        )

    logger.info("RUIAN bulk: %d inzerátů ke zpracování", len(rows))
    stats["total"] = len(rows)

    for row in rows:
        listing_id = row["id"]
        location_text = row["location_text"] or ""
        municipality = row["municipality"]

        lookup = await lookup_ruian_address(location_text, municipality)

        # Upsert do listing_cadastre_data – každý záznam samostatná transakce
        async with db_manager.acquire() as conn:
            await conn.execute(
                """
                INSERT INTO re_realestate.listing_cadastre_data
                    (listing_id, address_searched, ruian_kod, cadastre_url,
                     fetch_status, raw_ruian, fetched_at)
                VALUES ($1, $2, $3, $4, $5, $6::jsonb, NOW())
                ON CONFLICT (listing_id) DO UPDATE SET
                    address_searched = EXCLUDED.address_searched,
                    ruian_kod        = EXCLUDED.ruian_kod,
                    cadastre_url     = EXCLUDED.cadastre_url,
                    fetch_status     = EXCLUDED.fetch_status,
                    raw_ruian        = EXCLUDED.raw_ruian,
                    fetched_at       = NOW()
                """,
                listing_id,
                lookup["address_used"],
                lookup["ruian_kod"],
                lookup["cadastre_url"],
                lookup["fetch_status"],
                str(lookup["raw_ruian"]) if lookup["raw_ruian"] else None,
            )

        stats[lookup["fetch_status"]] += 1

        # Rate limiting – 1 req/s
        await asyncio.sleep(1.0)

    logger.info("RUIAN bulk hotovo: %s", stats)
    return stats
