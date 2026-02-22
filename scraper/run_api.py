#!/usr/bin/env python3
"""
FastAPI server startup script for RealEstate Scraper.

Usage:
    python run_api.py
    
Or with uvicorn directly:
    uvicorn api.main:app --host 0.0.0.0 --port 8001 --reload
"""
import uvicorn

if __name__ == "__main__":
    uvicorn.run(
        "api.main:app",
        host="0.0.0.0",
        port=8001,
        reload=True,  # Auto-reload při změnách kódu (dev mode)
        log_level="info"
    )
