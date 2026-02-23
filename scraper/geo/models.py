"""
Data models for the Route Corridor module.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional, List


@dataclass
class AreaConfig:
    """
    Configuration for a single route corridor area.

    Attributes:
        name:           Human-readable name for the area (used in logs / output).
        route_file:     Path to the route file (GPX or GeoJSON).
                        Relative paths are resolved from the config file's directory.
        buffer_km:      Buffer radius in kilometres on each side of the route.
        region_filter:  Optional NUTS-3 / LAU code to restrict results,
                        e.g. "CZ064" = Jihomoravský kraj.
        district_filter: Optional LAU-1 code of the district (okres),
                        e.g. "ZNO" = Znojmo, "BRV" = Brno-venkov.
        output_file:    Path where the resulting JSON will be written.
                        Relative paths are resolved from the config file's directory.
    """
    name: str
    route_file: str
    buffer_km: float
    region_filter: Optional[str]
    district_filter: Optional[str]
    output_file: str


@dataclass
class RootConfig:
    """Root configuration – list of areas to process."""
    areas: List[AreaConfig] = field(default_factory=list)


@dataclass
class Municipality:
    """
    A Czech municipality (obec) from the RÚIAN register.

    The RUIAN shapefile for municipalities uses these primary fields:
        KOD     – 6-digit RÚIAN code  (maps to ruian_id)
        NAZEV   – municipality name    (maps to name)
    District and region data come either from joined lookups or
    from the extended 'AdresniMisto' tables.
    """
    ruian_id: int
    name: str
    district_code: Optional[str] = None
    district_name: Optional[str] = None
    region_code: Optional[str] = None
    region_name: Optional[str] = None


@dataclass
class AreaOutput:
    """Result for a single area config – used by the generator."""
    area_name: str
    municipalities: List[Municipality] = field(default_factory=list)

    def to_json_list(self) -> List[dict]:
        return [
            {
                "ruianId": m.ruian_id,
                "name": m.name,
                "districtCode": m.district_code,
                "districtName": m.district_name,
                "regionCode": m.region_code,
                "regionName": m.region_name,
            }
            for m in self.municipalities
        ]
