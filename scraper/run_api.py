#!/usr/bin/env python3
"""
FastAPI server startup script for RealEstate Scraper.

Usage:
    python run_api.py
    
Or with uvicorn directly:
    uvicorn api.main:app --host 0.0.0.0 --port 8001 --reload
"""
import uvicorn
import os

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
