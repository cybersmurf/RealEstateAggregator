"""
Pydantic schemas for scraper API.
"""
from typing import List, Optional
from pydantic import BaseModel, ConfigDict, Field
from uuid import UUID
from datetime import datetime


class ScrapeTriggerRequest(BaseModel):
    """Request to trigger a scraping job.

    .NET API (ScrapingService) posílá camelCase (`sourceCodes`, `fullRescan`),
    Makefile a curl snake_case – bereme obojí. Do 30. 9. 2026 se camelCase tiše
    ignoroval: tlačítko v aplikaci vždy scrapovalo všechny zdroje bez full rescanu.
    """
    model_config = ConfigDict(populate_by_name=True)

    source_codes: Optional[List[str]] = Field(default=None, alias="sourceCodes")  # ["REMAX", "MMR"]
    full_rescan: bool = Field(default=False, alias="fullRescan")


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
