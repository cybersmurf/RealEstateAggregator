"""
Listing filters based on configuration and quality criteria.
Provides validation and filtering for listings before database insertion.
"""
import logging
from typing import Dict, Any, Optional, List
import yaml
from pathlib import Path

logger = logging.getLogger(__name__)


class FilterManager:
    """Manages filtering and validation of listings based on criteria."""
    
    def __init__(self, config_path: Optional[Path] = None):
        """
        Inicializuje FilterManager s konfigurací.
        
        Args:
            config_path: Cesta k settings.yaml. Pokud None, hledá v default lokaci.
        """
        if config_path is None:
            config_path = Path(__file__).parent.parent / "config" / "settings.yaml"
        
        if not config_path.exists():
            logger.warning(f"Config file not found: {config_path}. Using default configuration.")
            self.config = self._get_default_config()
        else:
            with open(config_path, "r") as f:
                self.config = yaml.safe_load(f) or {}
        
        self.search_filters = self.config.get("search_filters", {})
        self.quality_filters = self.config.get("quality_filters", {})
    
    def _get_default_config(self) -> Dict[str, Any]:
        """Vrátí default konfiguraci když config soubor neexistuje."""
        return {
            "search_filters": {
                "target_districts": ["Znojmo"],
                "houses": {"enabled": True, "max_price": 7500000},
                "land": {"enabled": True, "max_price": 2000000},
            },
            "quality_filters": {
                "require_photos": True,
                "require_price": True,
                "require_location": True,
            }
        }
    
    def should_include_listing(self, listing_data: Dict[str, Any]) -> tuple[bool, Optional[str]]:
        """
        Kontroluje, zda má být inzerát zahrnut (uložen do DB).
        
        Args:
            listing_data: Dictionary s daty listingu
            
        Returns:
            Tuple (should_include: bool, reason_if_excluded: str or None)
                - (True, None) pokud má být zahrnut
                - (False, "reason") pokud má být vyloučen
        """
        # Quality checks
        quality_result = self._check_quality_filters(listing_data)
        if quality_result[0] is False:
            return quality_result
        
        # Geographic and price checks
        search_result = self._check_search_filters(listing_data)
        if search_result[0] is False:
            return search_result
        
        return (True, None)
    
    def _check_quality_filters(self, listing_data: Dict[str, Any]) -> tuple[bool, Optional[str]]:
        """Kontroluje quality filtry."""
        qf = self.quality_filters
        
        # Fotky
        if qf.get("require_photos", False):
            photos = listing_data.get("photos", [])
            if not photos or len(photos) < qf.get("min_photos", 1):
                return (False, f"Insufficient photos (have {len(photos) if photos else 0})")
        
        # Cena
        if qf.get("require_price", False):
            if not listing_data.get("price"):
                return (False, "Missing price")
        
        # Lokace
        if qf.get("require_location", False):
            if not listing_data.get("location_text"):
                return (False, "Missing location")
        
        # Popis
        if qf.get("require_description", False):
            desc = listing_data.get("description", "")
            min_len = qf.get("min_description_length", 20)
            if not desc or len(desc) < min_len:
                return (False, f"Description too short (min {min_len} chars)")
        
        return (True, None)
    
    def _check_search_filters(self, listing_data: Dict[str, Any]) -> tuple[bool, Optional[str]]:
        """Kontroluje search filtry (geografické, cenové)."""
        sf = self.search_filters
        
        property_type = listing_data.get("property_type", "Ostatní")
        offer_type = listing_data.get("offer_type", "Sale")
        price = listing_data.get("price")
        location_text = listing_data.get("location_text", "").lower()
        
        # Kontroluj okres
        target_districts = sf.get("target_districts", [])
        if target_districts:
            district_match = any(
                district.lower() in location_text
                for district in target_districts
            )
            if not district_match:
                return (False, f"District not in target list: {location_text}")
        
        # Property type specific filters
        if property_type == "House":
            houses_filter = sf.get("houses", {})
            if not houses_filter.get("enabled", True):
                return (False, "Houses disabled")
            
            if houses_filter.get("offer_types") and offer_type not in houses_filter["offer_types"]:
                return (False, f"Offer type {offer_type} not in houses filter")
            
            if price:
                max_price = houses_filter.get("max_price")
                if max_price and price > max_price:
                    return (False, f"Price {price} exceeds max {max_price}")
                
                min_price = houses_filter.get("min_price")
                if min_price and price < min_price:
                    return (False, f"Price {price} below min {min_price}")
        
        elif property_type == "Land":
            land_filter = sf.get("land", {})
            if not land_filter.get("enabled", True):
                return (False, "Land disabled")
            
            if land_filter.get("offer_types") and offer_type not in land_filter["offer_types"]:
                return (False, f"Offer type {offer_type} not in land filter")
            
            if price:
                max_price = land_filter.get("max_price")
                if max_price and price > max_price:
                    return (False, f"Price {price} exceeds max {max_price}")
                
                min_price = land_filter.get("min_price")
                if min_price and price < min_price:
                    return (False, f"Price {price} below min {min_price}")
        
        elif property_type == "Apartment":
            apt_filter = sf.get("apartments", {})
            if not apt_filter.get("enabled", True):
                return (False, "Apartments disabled")
            
            if apt_filter.get("offer_types") and offer_type not in apt_filter["offer_types"]:
                return (False, f"Offer type {offer_type} not in apartments filter")
            
            if price:
                max_price = apt_filter.get("max_price")
                if max_price and price > max_price:
                    return (False, f"Price {price} exceeds max {max_price}")
                
                min_price = apt_filter.get("min_price")
                if min_price and price < min_price:
                    return (False, f"Price {price} below min {min_price}")
        
        elif property_type == "Commercial":
            com_filter = sf.get("commercial", {})
            if not com_filter.get("enabled", True):
                return (False, "Commercial disabled")
            
            if com_filter.get("offer_types") and offer_type not in com_filter["offer_types"]:
                return (False, f"Offer type {offer_type} not in commercial filter")
            
            if price:
                max_price = com_filter.get("max_price")
                if max_price and price > max_price:
                    return (False, f"Price {price} exceeds max {max_price}")
                
                min_price = com_filter.get("min_price")
                if min_price and price < min_price:
                    return (False, f"Price {price} below min {min_price}")
        
        return (True, None)
    
    def log_listing_decision(self, listing_data: Dict[str, Any], included: bool, reason: Optional[str]):
        """
        Loguje rozhodnutí o incluzi/exclusi listingu.
        
        Args:
            listing_data: Data listingu
            included: True pokud je zahrnut
            reason: Důvod exkluze (pokud excluded)
        """
        title = listing_data.get("title", "Unknown")
        url = listing_data.get("url", "N/A")
        
        if included:
            logger.debug(f"✓ INCLUDED listing: {title} ({url})")
        else:
            logger.debug(f"✗ EXCLUDED listing: {title} ({url}) - Reason: {reason}")


# Globální instance
_filter_manager: Optional[FilterManager] = None


def get_filter_manager() -> FilterManager:
    """Vrátí globální instanci FilterManager."""
    global _filter_manager
    if _filter_manager is None:
        _filter_manager = FilterManager()
    return _filter_manager


def init_filter_manager(config_path: Optional[Path] = None) -> FilterManager:
    """Inicializuje globální FilterManager."""
    global _filter_manager
    _filter_manager = FilterManager(config_path)
    return _filter_manager
