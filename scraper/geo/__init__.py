"""
geo – Route Corridor Municipality Generator

Generates a list of Czech municipalities (RÚIAN) that intersect
a buffer corridor around a given route (GPX / GeoJSON).

Typical usage:
    python -m geo.cli --config config/areas.yaml

Or from code:
    from geo.generator import AreaMunicipalityGenerator
    from geo.loader import GpxRouteLoader
    from geo.repository import ShapefileMunicipalityRepository
"""

from .models import AreaConfig, RootConfig, Municipality, AreaOutput

__all__ = ["AreaConfig", "RootConfig", "Municipality", "AreaOutput"]
