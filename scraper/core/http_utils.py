"""
Sdílené HTTP utility pro scrapery – retry s exponential backoff.

Použití v scraperech:
    from ..http_utils import http_retry

    @http_retry
    async def _fetch(self, url: str) -> str:
        response = await self._client.get(url)
        response.raise_for_status()
        return response.text
"""
import logging

import httpx
from tenacity import (
    retry,
    retry_if_exception_type,
    stop_after_attempt,
    wait_exponential,
    before_sleep_log,
)

logger = logging.getLogger(__name__)

# ─── Retry decorator ──────────────────────────────────────────────────────────
# Opakuje request při přechodných HTTP chybách (429 Too Many Requests, 503 Service Unavailable)
# nebo síťových chybách (ConnectError, TimeoutException).
# Max 3 pokusy, čeká exponenciálně 2 → 4 → 8 vteřin.
http_retry = retry(
    retry=retry_if_exception_type(
        (
            httpx.HTTPStatusError,   # 429, 503, 5xx
            httpx.ConnectError,      # síťová chyba
            httpx.TimeoutException,  # timeout
            httpx.RemoteProtocolError,
        )
    ),
    stop=stop_after_attempt(3),
    wait=wait_exponential(multiplier=1, min=2, max=10),
    before_sleep=before_sleep_log(logger, logging.WARNING),
    reraise=True,  # po 3 neúspěšných pokusech opětovně vyvolá výjimku
)
