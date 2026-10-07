"""
Slack notifikace pro scraping zdraví.
Odesílá upozornění když scraper vrátí 0 výsledků nebo data jsou stará.
"""
import logging
import statistics
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
        logger.warning("Slack notifikace selhala: %r", exc)
        return False


DROP_RATIO = 0.6          # pod 60 % obvyklého počtu = propad
DROP_MIN_MEDIAN = 20      # u malých zdrojů kolísání nehlásíme


def detect_drops(current: dict[str, int], history: dict[str, list[int]]) -> dict[str, tuple[int, int]]:
    """
    Zdroje, které vrátily výrazně míň než obvykle: {source: (teď, obvykle)}. „Obvykle" je medián
    posledních úspěšných běhů téhož režimu; potřebujeme aspoň dva. Rozbitý parser většinou nevrátí
    nulu ani výjimku, ale třeba 120 místo 2 900 (iDNES), a to by jinak nikdo neviděl.
    """
    drops: dict[str, tuple[int, int]] = {}
    for source, count in current.items():
        past = history.get(source) or []
        if len(past) < 2:
            continue
        usual = statistics.median(past)
        if usual >= DROP_MIN_MEDIAN and count < usual * DROP_RATIO:
            drops[source] = (count, int(usual))
    return drops


async def notify_job_summary(
    job_id: str,
    results: dict[str, int | Exception],
    full_rescan: bool,
    drops: dict[str, tuple[int, int]] | None = None,
    anomalies: dict[str, int] | None = None,
) -> None:
    """
    Odešle Slack zprávu po dokončení scraping jobu.

    Args:
        job_id:      UUID jobu
        results:     slovník {source_code: count_or_exception}
        full_rescan: zda šlo o full rescan
        drops:       zdroje s propadem výsledků proti minulým běhům (detect_drops)
        anomalies:   podezřelé hodnoty u nových inzerátů {pravidlo: počet}
    """
    if not _webhook_url:
        return

    failed = {k: v for k, v in results.items() if isinstance(v, Exception)}
    zero = {k for k, v in results.items() if v == 0 and not isinstance(v, Exception)}
    ok = {k: v for k, v in results.items() if isinstance(v, int) and v > 0}
    drops = drops or {}
    anomalies = anomalies or {}

    if not failed and not zero and not drops and not anomalies:
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
    if drops:
        lines.append("📉 *Propad výsledků* proti minulým běhům (možná změna webu): "
                     + ", ".join(f"`{s}` {now} (obvykle {usual})" for s, (now, usual) in sorted(drops.items())))
    if anomalies:
        lines.append("🧪 *Podezřelá data u nových inzerátů*: "
                     + ", ".join(f"{rule} {n}×" for rule, n in sorted(anomalies.items())))

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
        days = s.get("days_stale")
        # Nový zdroj bez jediného inzerátu má days_stale None (MAX(last_seen_at) je NULL)
        age = f"{days:.0f} dní bez aktualizace" if days is not None else "zatím žádná data"
        lines.append(
            f"  • `{s['code']}` ({s['name']}) – {age}, {s.get('active_count') or 0} aktivních inzerátů"
        )

    payload = {"text": "\n".join(lines), "mrkdwn": True}
    await _send(payload)
