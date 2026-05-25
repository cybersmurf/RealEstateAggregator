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
    retry_if_exception,
    retry_if_exception_type,
    stop_after_attempt,
    wait_exponential,
    before_sleep_log,
)

logger = logging.getLogger(__name__)


def _is_retryable(exc: BaseException) -> bool:
    """Retry jen na přechodné chyby: 429/5xx a síťové problémy. Ne na 4xx (404, 403…)."""
    if isinstance(exc, httpx.HTTPStatusError):
        return exc.response.status_code == 429 or exc.response.status_code >= 500
    return isinstance(exc, (httpx.ConnectError, httpx.TimeoutException, httpx.RemoteProtocolError))


# ─── Retry decorator ──────────────────────────────────────────────────────────
# Opakuje request při přechodných HTTP chybách (429 Too Many Requests, 503 Service Unavailable)
# nebo síťových chybách (ConnectError, TimeoutException).
# Max 3 pokusy, čeká exponenciálně 2 → 4 → 8 vteřin.
# 4xx chyby (404, 403…) se NEOPAKUJÍ.
http_retry = retry(
    retry=retry_if_exception(_is_retryable),
    stop=stop_after_attempt(3),
    wait=wait_exponential(multiplier=1, min=2, max=10),
    before_sleep=before_sleep_log(logger, logging.WARNING),
    reraise=True,  # po 3 neúspěšných pokusech opětovně vyvolá výjimku
)
