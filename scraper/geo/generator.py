"""
AreaMunicipalityGenerator – ties together the route loader, reprojection,
buffering and municipality lookup.

Reprojection strategy
---------------------
Route files use WGS-84 (lat/lon).  Buffering by a distance in metres requires
a metric CRS.  For Czech data, the best projection is EPSG:5514 (S-JTSK / Krovak
East North).  We use that as the bufferring CRS and then reproject the resulting
buffer polygon back to WGS-84 so the repository can work in a consistent CRS.

If pyproj is unavailable we fall back to a naive degree-based buffer
(≈ 1° ≈ 111 km) which is inaccurate; a warning is emitted.
"""
from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import List, Optional

from .loader import RouteLoader, create_loader
from .models import AreaConfig, AreaOutput, Municipality, RootConfig
from .repository import MunicipalityRepository

logger = logging.getLogger(__name__)

# Metric CRS used for buffering – S-JTSK is ideal for Czech Republic
_BUFFER_CRS = "EPSG:5514"
_WGS84_CRS  = "EPSG:4326"


class AreaMunicipalityGenerator:
    """
    Main entry point for generating municipality lists from route corridors.

    Parameters
    ----------
    municipality_repo:
        A MunicipalityRepository implementation (Shapefile or PostGIS).
    base_dir:
        Directory used to resolve relative paths in AreaConfig.
        Defaults to current working directory.
    """

    def __init__(
        self,
        municipality_repo: MunicipalityRepository,
        base_dir: Optional[str] = None,
    ) -> None:
        self._repo = municipality_repo
        self._base_dir = Path(base_dir) if base_dir else Path.cwd()

    # ---------------------------------------------------------------------- #
    #  Per-area processing                                                     #
    # ---------------------------------------------------------------------- #

    def generate_for_area(self, config: AreaConfig) -> AreaOutput:
        """
        Execute the full pipeline for one AreaConfig:
          load route → reproject → buffer → intersect → return AreaOutput.
        """
        route_path = self._resolve_path(config.route_file)
        logger.info("[%s] Loading route from %s …", config.name, route_path)

        loader: RouteLoader = create_loader(str(route_path))
        route_wgs84 = loader.load_route(str(route_path))
        logger.info("[%s] Route loaded: %d points.", config.name, len(route_wgs84.coords))

        buffer_wgs84 = self._create_buffer(
            route_wgs84, config.buffer_km, config.name
        )

        municipalities = self._repo.get_municipalities_intersecting(
            buffer_wgs84,
            config.region_filter,
            config.district_filter,
        )

        logger.info(
            "[%s] Found %d municipalities in %.1f km corridor.",
            config.name, len(municipalities), config.buffer_km
        )
        return AreaOutput(area_name=config.name, municipalities=municipalities)

    # ---------------------------------------------------------------------- #
    #  Batch processing + JSON output                                          #
    # ---------------------------------------------------------------------- #

    def run_all(self, root_config: RootConfig) -> List[AreaOutput]:
        """Process every AreaConfig in *root_config* and write JSON outputs."""
        outputs: List[AreaOutput] = []
        for area_config in root_config.areas:
            try:
                output = self.generate_for_area(area_config)
                self._write_output(output, area_config)
                outputs.append(output)
            except Exception as exc:
                logger.error("[%s] Failed: %s", area_config.name, exc, exc_info=True)
        return outputs

    # ---------------------------------------------------------------------- #
    #  Buffer helper                                                           #
    # ---------------------------------------------------------------------- #

    def _create_buffer(self, route_wgs84, buffer_km: float, area_name: str):
        """
        Reproject route to S-JTSK, buffer by *buffer_km* * 1000 m,
        then reproject the polygon back to WGS-84.
        """
        try:
            from pyproj import Transformer
            from shapely.ops import transform as shp_transform

            fwd = Transformer.from_crs(_WGS84_CRS, _BUFFER_CRS, always_xy=True)
            inv = Transformer.from_crs(_BUFFER_CRS, _WGS84_CRS, always_xy=True)

            route_m = shp_transform(fwd.transform, route_wgs84)
            buffer_m = route_m.buffer(buffer_km * 1000.0)
            buffer_wgs84 = shp_transform(inv.transform, buffer_m)

            logger.debug(
                "[%s] Buffer area: %.2f km²",
                area_name,
                buffer_m.area / 1_000_000,
            )
            return buffer_wgs84

        except ImportError:
            # Fallback: approximate 1 degree ≈ 111 km
            approx_deg = buffer_km / 111.0
            logger.warning(
                "[%s] pyproj not available – using naive degree-based buffer (%.4f°). "
                "Install pyproj for accurate metric buffering.",
                area_name, approx_deg
            )
            return route_wgs84.buffer(approx_deg)

    # ---------------------------------------------------------------------- #
    #  File I/O helpers                                                        #
    # ---------------------------------------------------------------------- #

    def _resolve_path(self, path_str: str) -> Path:
        p = Path(path_str)
        if not p.is_absolute():
            p = self._base_dir / p
        return p

    def _write_output(self, output: AreaOutput, config: AreaConfig) -> None:
        out_path = self._resolve_path(config.output_file)
        out_path.parent.mkdir(parents=True, exist_ok=True)

        payload = {
            "area": output.area_name,
            "bufferKm": config.buffer_km,
            "routeFile": config.route_file,
            "regionFilter": config.region_filter,
            "districtFilter": config.district_filter,
            "municipalityCount": len(output.municipalities),
            "municipalities": output.to_json_list(),
        }

        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)

        logger.info("[%s] Output written to %s", output.area_name, out_path)


# --------------------------------------------------------------------------- #
#  Config loader (YAML / JSON)                                                 #
# --------------------------------------------------------------------------- #

def load_config(path: str) -> RootConfig:
    """
    Load a RootConfig from a YAML or JSON file.

    YAML example::

        areas:
          - name: "Stitary-Pohorelice"
            routeFile: "routes/stitary-pohorelice.gpx"
            bufferKm: 5.0
            regionFilter: "CZ064"
            districtFilter: null
            outputFile: "output/stitary-pohorelice/municipalities.json"
    """
    import yaml  # already in requirements.txt (PyYAML)

    with open(path, "r", encoding="utf-8") as f:
        if path.endswith(".json"):
            import json as _json
            raw = _json.load(f)
        else:
            raw = yaml.safe_load(f)

    areas = [
        AreaConfig(
            name=a["name"],
            route_file=a["routeFile"],
            buffer_km=float(a["bufferKm"]),
            region_filter=a.get("regionFilter"),
            district_filter=a.get("districtFilter"),
            output_file=a["outputFile"],
        )
        for a in raw.get("areas", [])
    ]
    return RootConfig(areas=areas)
