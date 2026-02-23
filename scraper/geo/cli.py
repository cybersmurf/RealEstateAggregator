"""
CLI entry point for the Route Corridor Municipality Generator.

Usage examples:
  # Using the default config file (config/areas.yaml in the scraper directory):
  python -m geo.cli

  # Specify a config file explicitly:
  python -m geo.cli --config config/areas.yaml

  # Use the PostGIS backend instead of a shapefile:
  python -m geo.cli --config config/areas.yaml --backend postgis \\
      --dsn "postgresql://postgres:dev@localhost:5432/realestate_dev"

  # Use a specific shapefile:
  python -m geo.cli --config config/areas.yaml --shapefile data/ruian/obce.shp

  # Download RÚIAN data automatically before running:
  python -m geo.cli --download-ruian --shapefile data/ruian/

Run `python -m geo.cli --help` to see all options.
"""
from __future__ import annotations

import argparse
import logging
import os
import sys
from pathlib import Path

# Ensure the scraper root is on the path when run as __main__
_SCRAPER_ROOT = Path(__file__).resolve().parent.parent
if str(_SCRAPER_ROOT) not in sys.path:
    sys.path.insert(0, str(_SCRAPER_ROOT))

from geo.generator import AreaMunicipalityGenerator, load_config
from geo.repository import MunicipalityRepository, PostgisMunicipalityRepository, ShapefileMunicipalityRepository


# --------------------------------------------------------------------------- #
#  RÚIAN download helper                                                       #
# --------------------------------------------------------------------------- #

def download_ruian(dest_dir: str) -> str:
    """
    Download the RÚIAN 'Obce' shapefile from ČÚZK GeoPortal.

    The download uses the ATOM feed / direct download endpoint that ČÚZK provides
    as open data (CC-BY 4.0).  The result is a ZIP containing the SHP, DBF, PRJ, etc.

    Returns the path to the extracted SHP file.
    """
    import zipfile
    import urllib.request

    dest = Path(dest_dir)
    dest.mkdir(parents=True, exist_ok=True)

    # Official ČÚZK RÚIAN open-data download for municipalities (Obce) – WGS84 version
    # Source: https://geoportal.cuzk.cz/Default.aspx?mode=TextMeta&side=dSady_RUIAN
    #
    # The URL below gives the entire CZ dataset.  For production use you can also
    # download per-county ZIPs if only Jihomoravský kraj is needed.
    url = (
        "https://vdp.cuzk.cz/vdp/ruian/vymennyformat/vf.zip"
        "?akceslovnik=OB"
        "&typdat=S"
        "&pudoagie=A"
        "&stahkraj=&stahokres=&stahobec="
    )

    zip_path = dest / "ruian_obce.zip"
    logging.getLogger(__name__).info("Downloading RÚIAN 'Obce' from ČÚZK …")
    logging.getLogger(__name__).info("URL: %s", url)

    urllib.request.urlretrieve(url, zip_path)
    logging.getLogger(__name__).info("Downloaded to %s", zip_path)

    with zipfile.ZipFile(zip_path, "r") as z:
        z.extractall(dest)
    logging.getLogger(__name__).info("Extracted to %s", dest)

    # Look for the SHP in the extracted files
    shp_files = list(dest.rglob("*.shp"))
    if not shp_files:
        raise FileNotFoundError(f"No .shp file found in downloaded ZIP at {dest}")

    # Prefer a file with 'obce' or 'OB' in its name
    for shp in shp_files:
        if "ob" in shp.stem.lower():
            return str(shp)
    return str(shp_files[0])


# --------------------------------------------------------------------------- #
#  Argument parser                                                              #
# --------------------------------------------------------------------------- #

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="python -m geo.cli",
        description=(
            "Generate a list of Czech municipalities (RÚIAN) that intersect\n"
            "a buffer corridor around a given route (GPX / GeoJSON)."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )

    parser.add_argument(
        "--config",
        default="config/areas.yaml",
        metavar="PATH",
        help="Path to the YAML (or JSON) areas config file. "
             "Default: config/areas.yaml",
    )

    # Backend selection
    backend_group = parser.add_argument_group("Backend (data source for municipalities)")
    backend_group.add_argument(
        "--backend",
        choices=["shapefile", "postgis"],
        default="shapefile",
        help="Which repository backend to use. Default: shapefile",
    )
    backend_group.add_argument(
        "--shapefile",
        default="data/ruian/obce.shp",
        metavar="PATH",
        help="Path to the RÚIAN Obce shapefile (.shp or .gpkg). "
             "Only used when --backend=shapefile. Default: data/ruian/obce.shp",
    )
    backend_group.add_argument(
        "--crs-epsg",
        type=int,
        default=None,
        metavar="EPSG",
        help="Override the CRS EPSG code of the shapefile (e.g. 5514 or 4326). "
             "If omitted, read from the .prj sidecar.",
    )
    backend_group.add_argument(
        "--dsn",
        default=None,
        metavar="DSN",
        help="PostgreSQL connection string for the PostGIS backend. "
             "E.g. 'postgresql://postgres:dev@localhost:5432/realestate_dev'",
    )
    backend_group.add_argument(
        "--pg-schema",
        default="geo",
        metavar="SCHEMA",
        help="PostGIS schema containing the ruian_obce table. Default: geo",
    )
    backend_group.add_argument(
        "--pg-table",
        default="ruian_obce",
        metavar="TABLE",
        help="PostGIS table name. Default: ruian_obce",
    )

    # Utilities
    parser.add_argument(
        "--download-ruian",
        action="store_true",
        help="Download the RÚIAN Obce shapefile from ČÚZK before processing. "
             "The file will be saved to the directory specified by --shapefile.",
    )
    parser.add_argument(
        "--list",
        action="store_true",
        help="List all area configs defined in the config file and exit.",
    )
    parser.add_argument(
        "--area",
        metavar="NAME",
        help="Process only the area with this name (substring match).",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity. Default: INFO",
    )

    return parser


# --------------------------------------------------------------------------- #
#  Main                                                                        #
# --------------------------------------------------------------------------- #

def main(argv=None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s  %(levelname)-8s  %(name)s  %(message)s",
        datefmt="%H:%M:%S",
    )

    log = logging.getLogger("geo.cli")

    # ------------------------------------------------------------------ #
    #  Load config                                                         #
    # ------------------------------------------------------------------ #
    config_path = Path(args.config)
    if not config_path.exists():
        log.error("Config file not found: %s", config_path)
        return 1

    root_config = load_config(str(config_path))
    base_dir = str(config_path.parent)

    if args.list:
        print(f"Areas defined in {config_path}:")
        for area in root_config.areas:
            print(f"  • {area.name}  (route: {area.route_file}, buffer: {area.buffer_km} km)")
        return 0

    if args.area:
        root_config.areas = [
            a for a in root_config.areas
            if args.area.lower() in a.name.lower()
        ]
        if not root_config.areas:
            log.error("No area matching '%s' found.", args.area)
            return 1

    # ------------------------------------------------------------------ #
    #  Download RÚIAN (optional)                                          #
    # ------------------------------------------------------------------ #
    shapefile_path = args.shapefile
    if args.download_ruian:
        dest = str(Path(shapefile_path).parent)
        try:
            shapefile_path = download_ruian(dest)
            log.info("RÚIAN downloaded: %s", shapefile_path)
        except Exception as exc:
            log.error("RÚIAN download failed: %s", exc)
            return 1

    # ------------------------------------------------------------------ #
    #  Build repository                                                   #
    # ------------------------------------------------------------------ #
    repo: MunicipalityRepository

    if args.backend == "postgis":
        dsn = args.dsn or os.environ.get("DATABASE_URL") or os.environ.get("POSTGIS_DSN")
        if not dsn:
            log.error(
                "PostGIS backend requires --dsn or DATABASE_URL / POSTGIS_DSN env var."
            )
            return 1
        repo = PostgisMunicipalityRepository(
            dsn=dsn,
            schema=args.pg_schema,
            table=args.pg_table,
        )
        log.info("Using PostGIS backend (%s.%s)", args.pg_schema, args.pg_table)

    else:  # shapefile
        shp = Path(shapefile_path)
        if not shp.exists():
            log.error(
                "Shapefile not found: %s\n"
                "Use --download-ruian to fetch it automatically, or\n"
                "download manually from https://geoportal.cuzk.cz/ and "
                "specify the path with --shapefile.",
                shp,
            )
            return 1
        repo = ShapefileMunicipalityRepository(
            shapefile_path=str(shp),
            crs_epsg=args.crs_epsg,
        )
        log.info("Using Shapefile backend: %s", shp)

    # ------------------------------------------------------------------ #
    #  Run generator                                                      #
    # ------------------------------------------------------------------ #
    generator = AreaMunicipalityGenerator(
        municipality_repo=repo,
        base_dir=base_dir,
    )

    outputs = generator.run_all(root_config)

    # ------------------------------------------------------------------ #
    #  Summary                                                            #
    # ------------------------------------------------------------------ #
    print("\n── Summary ──")
    for out in outputs:
        area_cfg = next(a for a in root_config.areas if a.name == out.area_name)
        print(f"  {out.area_name}: {len(out.municipalities)} municipalities → {area_cfg.output_file}")
        for m in out.municipalities[:5]:
            print(f"    • {m.name}")
        if len(out.municipalities) > 5:
            print(f"    … and {len(out.municipalities) - 5} more")

    return 0


if __name__ == "__main__":
    sys.exit(main())
