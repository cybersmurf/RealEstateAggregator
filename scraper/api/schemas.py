"""
Pydantic schemas for scraper API.
"""
from typing import List, Optional
from pydantic import BaseModel
from uuid import UUID
from datetime import datetime


class ScrapeTriggerRequest(BaseModel):
    """Request to trigger a scraping job."""
    source_codes: Optional[List[str]] = None  # ["REMAX", "MMR"]
    full_rescan: bool = False


class ScrapeTriggerResponse(BaseModel):
    """Response from triggering a scraping job."""
    job_id: UUID
    status: str = "Queued"  # Queued, Started, Failed
    message: Optional[str] = None


class ScrapeJob(BaseModel):
    """Scraping job metadata."""
    job_id: UUID
    source_codes: Optional[List[str]] = None
    full_rescan: bool = False
    created_at: datetime
    status: str = "Queued"
    error_message: Optional[str] = None
