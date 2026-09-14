#!/usr/bin/env python3
"""
FastAPI server startup script for RealEstate Scraper.

Usage:
    python run_api.py
    
Or with uvicorn directly:
    uvicorn api.main:app --host 0.0.0.0 --port 8001 --reload
"""
import logging
import os

import uvicorn

# uvicorn(log_level=...) nastavuje jen loggery uvicorn.*. Loggery scraperů
# (core.*) by bez root handleru pouštěly jen WARNING+ přes lastResort, takže
# v `docker logs` nebylo vidět, co který běh stáhl, vyřadil nebo deaktivoval.
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
)
# httpx loguje každý request na INFO — při scrapu tisíce řádků
logging.getLogger("httpx").setLevel(logging.WARNING)
logging.getLogger("httpcore").setLevel(logging.WARNING)

if __name__ == "__main__":
    # reload=True je vhodný pouze pro lokální vývoj mimo Docker
    # V Docker kontejneru způsobuje hang na macOS → RELOAD=0 nebo DOCKER=1
    in_docker = os.getenv("DOCKER", "0") == "1" or os.getenv("RELOAD", "1") == "0"
    reload_mode = not in_docker
    uvicorn.run(
        "api.main:app",
        host="0.0.0.0",
        port=8001,
        reload=reload_mode,
        log_level="info"
    )
