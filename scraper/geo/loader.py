"""
Route loaders – convert GPX / GeoJSON files to a Shapely LineString in WGS-84.

Supported formats:
  - GPX  (.gpx)  – track points (trk/trkpt) and/or route points (rte/rtept)
  - GeoJSON (.geojson / .json) – Feature with LineString / MultiLineString geometry

All loaders return coordinates in WGS-84 (EPSG:4326), i.e. (lon, lat) tuples,
which is the convention Shapely expects when paired with pyproj's always_xy=True.
"""
from __future__ import annotations

import json
import os
from abc import ABC, abstractmethod
from pathlib import Path
from typing import List, Tuple

from shapely.geometry import LineString, MultiLineString


# --------------------------------------------------------------------------- #
#  Abstract interface                                                           #
# --------------------------------------------------------------------------- #

class RouteLoader(ABC):
    """Load a route file and return a Shapely LineString in WGS-84."""

    @abstractmethod
    def load_route(self, route_file: str) -> LineString:
        ...


# --------------------------------------------------------------------------- #
#  GPX loader                                                                   #
# --------------------------------------------------------------------------- #

class GpxRouteLoader(RouteLoader):
    """
    Parses a GPX file using the *gpxpy* library.

    Preference order for coordinate extraction:
      1. Tracks (trk → trkseg → trkpt)  – recorded GPS traces
      2. Routes (rte → rtept)           – planned routes
      3. Waypoints (wpt)                – fallback for single-segment paths

    All points are merged into a single LineString in the order they appear.
    """

    def load_route(self, route_file: str) -> LineString:
        try:
            import gpxpy
        except ImportError as exc:
            raise ImportError(
                "gpxpy is required for GPX loading. "
                "Install it with: pip install gpxpy"
            ) from exc

        path = Path(route_file)
        if not path.exists():
            raise FileNotFoundError(f"GPX file not found: {path}")

        with open(path, "r", encoding="utf-8") as f:
            gpx = gpxpy.parse(f)

        coords: List[Tuple[float, float]] = []  # (lon, lat)

        # 1. Tracks
        for track in gpx.tracks:
            for segment in track.segments:
                for point in segment.points:
                    coords.append((point.longitude, point.latitude))

        # 2. Routes (if no track points found)
        if not coords:
            for route in gpx.routes:
                for point in route.points:
                    coords.append((point.longitude, point.latitude))

        # 3. Waypoints (last resort)
        if not coords:
            for wpt in gpx.waypoints:
                coords.append((wpt.longitude, wpt.latitude))

        if len(coords) < 2:
            raise ValueError(
                f"GPX file '{path}' contains fewer than 2 points "
                f"(found {len(coords)}). Cannot build a LineString."
            )

        return LineString(coords)


# --------------------------------------------------------------------------- #
#  GeoJSON loader                                                               #
# --------------------------------------------------------------------------- #

class GeoJsonRouteLoader(RouteLoader):
    """
    Loads a route from a GeoJSON file.

    Accepted geometry types:
      - LineString           – used directly
      - MultiLineString      – merged into a single LineString (segments joined end-to-end)
      - Feature              – the geometry field is extracted automatically
      - FeatureCollection    – first feature's geometry is used (with a warning if >1 feature)

    Coordinates must be in WGS-84 [lon, lat] or [lon, lat, ele] order.
    """

    def load_route(self, route_file: str) -> LineString:
        path = Path(route_file)
        if not path.exists():
            raise FileNotFoundError(f"GeoJSON file not found: {path}")

        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)

        geom = self._extract_geometry(data, path)
        coords = self._geom_to_coords(geom, path)

        if len(coords) < 2:
            raise ValueError(
                f"GeoJSON file '{path}' contains fewer than 2 points "
                f"(found {len(coords)}). Cannot build a LineString."
            )

        return LineString(coords)

    def _extract_geometry(self, data: dict, path: Path) -> dict:
        gtype = data.get("type")

        if gtype == "FeatureCollection":
            features = data.get("features", [])
            if not features:
                raise ValueError(f"GeoJSON FeatureCollection in '{path}' has no features.")
            if len(features) > 1:
                import warnings
                warnings.warn(
                    f"GeoJSON FeatureCollection in '{path}' has {len(features)} features; "
                    "using only the first one.",
                    stacklevel=3,
                )
            return features[0]["geometry"]

        if gtype == "Feature":
            return data["geometry"]

        if gtype in ("LineString", "MultiLineString"):
            return data

        raise ValueError(
            f"Unsupported GeoJSON geometry type '{gtype}' in '{path}'. "
            "Expected LineString, MultiLineString, Feature, or FeatureCollection."
        )

    def _geom_to_coords(
        self, geom: dict, path: Path
    ) -> List[Tuple[float, float]]:
        gtype = geom.get("type")
        raw_coords = geom.get("coordinates", [])

        if gtype == "LineString":
            return [(c[0], c[1]) for c in raw_coords]

        if gtype == "MultiLineString":
            # Join all segments end-to-end (assumes they are ordered)
            coords: List[Tuple[float, float]] = []
            for segment in raw_coords:
                coords.extend((c[0], c[1]) for c in segment)
            return coords

        raise ValueError(
            f"Unexpected inner geometry type '{gtype}' while parsing '{path}'."
        )


# --------------------------------------------------------------------------- #
#  Auto-detecting factory                                                       #
# --------------------------------------------------------------------------- #

def create_loader(route_file: str) -> RouteLoader:
    """
    Returns the appropriate RouteLoader based on the file extension.

    Supports: .gpx → GpxRouteLoader
              .geojson / .json → GeoJsonRouteLoader
    """
    ext = os.path.splitext(route_file)[-1].lower()
    if ext == ".gpx":
        return GpxRouteLoader()
    if ext in (".geojson", ".json"):
        return GeoJsonRouteLoader()
    raise ValueError(
        f"Cannot determine loader for extension '{ext}'. "
        "Supported: .gpx, .geojson, .json"
    )
