"""
Municipality repositories – load RÚIAN obec data and return those that intersect
a given buffer geometry.

Two backends are provided:
  1. ShapefileMunicipalityRepository  – reads a local RÚIAN SHP file via GeoPandas
  2. PostgisMunicipalityRepository    – queries PostGIS via psycopg / asyncpg

RÚIAN resource:
  Download the 'Obce' (obec) shapefile from the ČÚZK GeoPortal:
    https://geoportal.cuzk.cz/Default.aspx?mode=TextMeta&side=dSady_RUIAN&metadataID=CZ-00025712-CUZK_RUIAN-OB-SHP
  or use the WFS endpoint:
    https://services.cuzk.cz/wfs/inspire-cp-wfs.asp

  Expected column names in the SHP (RÚIAN 'Obce' export):
    KOD      – 6-digit RUIAN code
    NAZEV    – municipality name
    OKRES_LAU1  / KOD_OKRES   – district code (present in enriched exports)
    KRAJ_NUTS3  / KOD_KRAJ    – region code (NUTS-3, e.g. CZ064)

  If district/region columns are missing, a join with the 'Okresy' SHP is needed.
  See scripts/download_ruian.sh for an automated download helper.
"""
from __future__ import annotations

import logging
from abc import ABC, abstractmethod
from typing import List, Optional

from shapely.geometry.base import BaseGeometry

from .models import Municipality

logger = logging.getLogger(__name__)


# --------------------------------------------------------------------------- #
#  Column name aliases                                                          #
#  RÚIAN SHP exports differ between versions; we handle the most common ones.  #
# --------------------------------------------------------------------------- #

_RUIAN_ID_COLS = ["KOD", "kod", "RUIAN_ID", "ruian_id"]
_NAME_COLS = ["NAZEV", "nazev", "NAME", "name"]
_DISTRICT_CODE_COLS = ["OKRES_LAU1", "KOD_OKRES", "okres_lau1", "kod_okres", "DISTRICT_CODE"]
_DISTRICT_NAME_COLS = ["OKRES_NAZEV", "NAZEV_OKRESU", "okres_nazev", "DISTRICT_NAME"]
_REGION_CODE_COLS = ["KRAJ_NUTS3", "KOD_KRAJ", "kraj_nuts3", "kod_kraj", "REGION_CODE"]
_REGION_NAME_COLS = ["KRAJ_NAZEV", "NAZEV_KRAJE", "kraj_nazev", "REGION_NAME"]


def _first_col(df, candidates: list[str]) -> Optional[str]:
    """Return the first column name from *candidates* that exists in *df*."""
    for c in candidates:
        if c in df.columns:
            return c
    return None


# --------------------------------------------------------------------------- #
#  Abstract interface                                                           #
# --------------------------------------------------------------------------- #

class MunicipalityRepository(ABC):
    @abstractmethod
    def get_municipalities_intersecting(
        self,
        buffer_wgs84: BaseGeometry,
        region_filter: Optional[str] = None,
        district_filter: Optional[str] = None,
    ) -> List[Municipality]:
        ...


# --------------------------------------------------------------------------- #
#  Shapefile backend                                                            #
# --------------------------------------------------------------------------- #

class ShapefileMunicipalityRepository(MunicipalityRepository):
    """
    Reads a RÚIAN 'Obce' SHP / GeoPackage and performs a spatial intersection.

    Parameters
    ----------
    shapefile_path:
        Path to the SHP (or .gpkg) file with municipality polygons.
    crs_epsg:
        EPSG code of the CRS used in the shapefile.
        RÚIAN exports are most commonly:
          5514  – S-JTSK / Krovak East North (default for ČÚZK downloads)
          4326  – WGS-84 (lat/lon)
        If None the CRS is read from the file's .prj sidecar.
    """

    def __init__(self, shapefile_path: str, crs_epsg: Optional[int] = None) -> None:
        self._shapefile_path = shapefile_path
        self._crs_epsg = crs_epsg
        self._gdf = None  # lazy-loaded

    # ---------------------------------------------------------------------- #
    #  Lazy load + reproject to WGS-84                                        #
    # ---------------------------------------------------------------------- #

    def _load(self):
        try:
            import geopandas as gpd
        except ImportError as exc:
            raise ImportError(
                "geopandas is required for the Shapefile backend. "
                "Install it with: pip install geopandas"
            ) from exc

        logger.info("Loading RÚIAN shapefile from %s …", self._shapefile_path)
        gdf = gpd.read_file(self._shapefile_path)

        if self._crs_epsg:
            gdf = gdf.set_crs(f"EPSG:{self._crs_epsg}", allow_override=True)

        if gdf.crs is None:
            logger.warning(
                "Shapefile has no CRS defined. Assuming EPSG:5514 (S-JTSK)."
            )
            gdf = gdf.set_crs("EPSG:5514")

        if gdf.crs.to_epsg() != 4326:
            logger.info("Reprojecting RÚIAN data from %s to WGS-84 …", gdf.crs)
            gdf = gdf.to_crs("EPSG:4326")

        self._gdf = gdf
        logger.info("Loaded %d municipalities.", len(gdf))

    # ---------------------------------------------------------------------- #
    #  Public query                                                            #
    # ---------------------------------------------------------------------- #

    def get_municipalities_intersecting(
        self,
        buffer_wgs84: BaseGeometry,
        region_filter: Optional[str] = None,
        district_filter: Optional[str] = None,
    ) -> List[Municipality]:
        if self._gdf is None:
            self._load()

        gdf = self._gdf

        # --- optional attribute filters ------------------------------------ #
        region_col = _first_col(gdf, _REGION_CODE_COLS)
        district_col = _first_col(gdf, _DISTRICT_CODE_COLS)

        if region_filter and region_col:
            gdf = gdf[gdf[region_col] == region_filter]
            logger.debug("After region filter '%s': %d rows", region_filter, len(gdf))
        elif region_filter:
            logger.warning(
                "region_filter='%s' requested but no region column found in shapefile. "
                "Available columns: %s", region_filter, list(gdf.columns)
            )

        if district_filter and district_col:
            gdf = gdf[gdf[district_col] == district_filter]
            logger.debug("After district filter '%s': %d rows", district_filter, len(gdf))
        elif district_filter:
            logger.warning(
                "district_filter='%s' requested but no district column found in shapefile. "
                "Available columns: %s", district_filter, list(gdf.columns)
            )

        # --- spatial intersection ------------------------------------------ #
        intersecting = gdf[gdf.geometry.intersects(buffer_wgs84)]
        logger.info("Municipalities intersecting buffer: %d", len(intersecting))

        # --- map to domain model ------------------------------------------- #
        id_col = _first_col(intersecting, _RUIAN_ID_COLS)
        name_col = _first_col(intersecting, _NAME_COLS)
        dist_name_col = _first_col(intersecting, _DISTRICT_NAME_COLS)
        reg_name_col = _first_col(intersecting, _REGION_NAME_COLS)

        results: List[Municipality] = []
        for _, row in intersecting.iterrows():
            results.append(Municipality(
                ruian_id=int(row[id_col]) if id_col else 0,
                name=str(row[name_col]) if name_col else "?",
                district_code=str(row[district_col]) if district_col else None,
                district_name=str(row[dist_name_col]) if dist_name_col else None,
                region_code=str(row[region_col]) if region_col else None,
                region_name=str(row[reg_name_col]) if reg_name_col else None,
            ))

        return sorted(results, key=lambda m: m.name)


# --------------------------------------------------------------------------- #
#  PostGIS backend                                                             #
# --------------------------------------------------------------------------- #

class PostgisMunicipalityRepository(MunicipalityRepository):
    """
    Queries a PostGIS table that holds RÚIAN municipality polygons.

    The table is expected to live in the database specified by *dsn* and have
    at minimum these columns (snake_case after import):
        id / ruian_id   – BIGINT
        name            – TEXT
        district_code   – TEXT  (nullable)
        district_name   – TEXT  (nullable)
        region_code     – TEXT  (nullable)  e.g. "CZ064"
        region_name     – TEXT  (nullable)
        geometry        – GEOMETRY(MultiPolygon, 4326) or similar

    Schema can be overridden via *schema*. If you import from a RÚIAN SHP file,
    the script scripts/import_ruian_to_postgis.sh handles the load.

    Parameters
    ----------
    dsn:
        libpq connection string, e.g.
        "postgresql://postgres:dev@localhost:5432/realestate_dev"
    table:
        Table name (without schema).
    schema:
        Database schema name.
    geom_col:
        Name of the geometry column.
    srid:
        SRID of the geometry column (used for ST_Transform).
    """

    def __init__(
        self,
        dsn: str,
        table: str = "ruian_obce",
        schema: str = "geo",
        geom_col: str = "geometry",
        srid: int = 4326,
    ) -> None:
        self._dsn = dsn
        self._table = table
        self._schema = schema
        self._geom_col = geom_col
        self._srid = srid

    def get_municipalities_intersecting(
        self,
        buffer_wgs84: BaseGeometry,
        region_filter: Optional[str] = None,
        district_filter: Optional[str] = None,
    ) -> List[Municipality]:
        try:
            import psycopg
        except ImportError as exc:
            raise ImportError(
                "psycopg (v3) is required for the PostGIS backend. "
                "Install it with: pip install psycopg[binary]"
            ) from exc

        wkt = buffer_wgs84.wkt

        # Build optional WHERE clauses
        where_parts = [
            f"ST_Intersects({self._geom_col}, "
            f"ST_Transform(ST_GeomFromText(:wkt, 4326), {self._srid}))"
        ]
        params: dict = {"wkt": wkt}

        if region_filter:
            where_parts.append("region_code = :region_code")
            params["region_code"] = region_filter

        if district_filter:
            where_parts.append("district_code = :district_code")
            params["district_code"] = district_filter

        where_sql = " AND ".join(where_parts)
        sql = f"""
            SELECT
                COALESCE(ruian_id, 0)   AS ruian_id,
                name,
                district_code,
                district_name,
                region_code,
                region_name
            FROM {self._schema}.{self._table}
            WHERE {where_sql}
            ORDER BY name
        """

        results: List[Municipality] = []
        with psycopg.connect(self._dsn) as conn:
            with conn.cursor() as cur:
                # psycopg v3 uses %s placeholders; convert :name → %s style
                sql_pg, values = _named_to_positional(sql, params)
                cur.execute(sql_pg, values)
                for row in cur.fetchall():
                    results.append(Municipality(
                        ruian_id=row[0],
                        name=row[1],
                        district_code=row[2],
                        district_name=row[3],
                        region_code=row[4],
                        region_name=row[5],
                    ))

        logger.info("PostGIS returned %d municipalities.", len(results))
        return results


def _named_to_positional(sql: str, params: dict) -> tuple[str, list]:
    """Convert ':name' placeholders to '%s' and return (sql, [values])."""
    import re
    values = []
    def replace(match):
        key = match.group(1)
        values.append(params[key])
        return "%s"
    converted = re.sub(r":(\w+)", replace, sql)
    return converted, values
