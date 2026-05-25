"""
Slack notifikace pro scraping zdraví.
Odesílá upozornění když scraper vrátí 0 výsledků nebo data jsou stará.
"""
import logging
import os
from typing import Any

import httpx

logger = logging.getLogger(__name__)

# Env var přepíše settings.yaml
_webhook_url: str | None = None


def configure(webhook_url: str | None) -> None:
    """Nastav Slack webhook URL (voláno při startu z settings.yaml nebo env)."""
    global _webhook_url
    env_url = os.getenv("SLACK_WEBHOOK_URL")
    _webhook_url = env_url or webhook_url or None
    if _webhook_url:
        logger.info("✓ Slack notifikace nakonfigurovány")
    else:
        logger.info("Slack notifikace vypnuty (SLACK_WEBHOOK_URL není nastaven)")


def is_configured() -> bool:
    return bool(_webhook_url)


async def _send(payload: dict[str, Any]) -> bool:
    """Odešle payload na Slack Incoming Webhook. Vrátí True při úspěchu."""
    if not _webhook_url:
        return False
    try:
        async with httpx.AsyncClient(timeout=10) as client:
            resp = await client.post(_webhook_url, json=payload)
            resp.raise_for_status()
        return True
    except Exception as exc:
        logger.warning(f"Slack notifikace selhala: {exc}")
        return False


async def notify_job_summary(
    job_id: str,
    results: dict[str, int | Exception],
    full_rescan: bool,
) -> None:
    """
    Odešle Slack zprávu po dokončení scraping jobu.

    Args:
        job_id:      UUID jobu
        results:     slovník {source_code: count_or_exception}
        full_rescan: zda šlo o full rescan
    """
    if not _webhook_url:
        return

    failed = {k: v for k, v in results.items() if isinstance(v, Exception)}
    zero = {k for k, v in results.items() if v == 0 and not isinstance(v, Exception)}
    ok = {k: v for k, v in results.items() if isinstance(v, int) and v > 0}

    if not failed and not zero:
        # Vše OK – neposílej nic (zbytečný noise)
        return

    mode = "full rescan" if full_rescan else "inkrementální"
    total_ok = sum(ok.values())

    lines = [f"*Scraping job dokončen* ({mode}) | job_id: `{job_id[:8]}…`"]
    lines.append(f"✅ OK: {len(ok)} zdrojů, {total_ok} inzerátů")

    if zero:
        lines.append(f"⚠️ *0 výsledků* (možná rozbitý scraper): {', '.join(f'`{s}`' for s in sorted(zero))}")
    if failed:
        for src, exc in failed.items():
            lines.append(f"🔴 *{src}* selhal: `{type(exc).__name__}: {exc}`")

    payload = {
        "text": "\n".join(lines),
        "mrkdwn": True,
    }
    await _send(payload)


async def notify_stale_sources(stale: list[dict[str, Any]]) -> None:
    """
    Odešle upozornění na zdroje, jejichž data jsou příliš stará.
    Voláno z health check endpointu nebo pravidelně.

    Args:
        stale: seznam slovníků se klíči code, name, days_stale, active_count
    """
    if not _webhook_url or not stale:
        return

    lines = [f"⚠️ *{len(stale)} mrtvý/ch scraper/ů* – data jsou zastaralá:"]
    for s in stale:
        lines.append(
            f"  • `{s['code']}` ({s['name']}) – {s['days_stale']:.0f} dní bez aktualizace, {s['active_count']} aktivních inzerátů"
        )

    payload = {"text": "\n".join(lines), "mrkdwn": True}
    await _send(payload)
