#!/usr/bin/env python3
"""
Mistral Batch Inference – hromadné AI joby přes Mistral Batch API
=================================================================
Náhrada za volání /api/ollama/bulk-* endpointů po jednom záznamu.
Mistral Batch API nabízí ~50% slevu a žádný rate-limit throttling.

Použití:
    # Hromadná normalizace (do 1558 inzerátů najednou)
    python scripts/mistral_batch.py normalize --batch-size 200

    # Smart tags
    python scripts/mistral_batch.py smart-tags --batch-size 200

    # Cenový signál
    python scripts/mistral_batch.py price-signal --batch-size 200

    # Kombinace všeho (spustí pořadí: normalize → smart-tags → price-signal)
    python scripts/mistral_batch.py all

Požadavky:
    pip install mistralai httpx python-dotenv

Konfigurace (env vars nebo .env soubor):
    MISTRAL_API_KEY=sk-...
    REALESTATE_API_URL=http://localhost:5001  (nebo http://localhost:5001 pro Docker)
"""

import os
import sys
import json
import time
import argparse
import logging
from typing import Any

import httpx

try:
    from mistralai import Mistral
except ImportError:
    print("Chybí balíček 'mistralai'. Instaluj: pip install mistralai")
    sys.exit(1)

# ─── Konfigurace ──────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger("mistral-batch")

MISTRAL_API_KEY = os.getenv("MISTRAL_API_KEY") or ""
MISTRAL_MODEL   = os.getenv("MISTRAL_BATCH_MODEL", "mistral-large-2512")
API_BASE_URL    = os.getenv("REALESTATE_API_URL", "http://localhost:5001")

NORMALIZE_SYSTEM = """
You are a Czech real estate data extractor.
From the listing description, extract structured data as JSON.
Respond ONLY with valid JSON (no explanation):
{
  "year_built": 1985,
  "floor": 2,
  "total_floors": 4,
  "has_elevator": false,
  "has_basement": true,
  "has_garage": false,
  "has_garden": false,
  "has_balcony": false,
  "has_terrace": false,
  "has_pool": false,
  "heating_type": "gas",
  "energy_class": "C",
  "ownership": "personal",
  "is_single_floor": false,
  "has_storage": false,
  "extension_possible": false
}
Use null for unknown values. heating_type: gas|electric|solid_fuel|heat_pump|district|other.
ownership: personal|cooperative|company|state.
energy_class: A|B|C|D|E|F|G or null.
""".strip()

SMART_TAGS_SYSTEM = """
You are a Czech real estate data extractor.
Extract exactly 5 short keyword tags from the listing description.
Tags must be in Czech, lowercase, max 2 words each.
Focus on: property features, amenities, construction type, condition, extras.
Respond ONLY with valid JSON array: ["tag1","tag2","tag3","tag4","tag5"]
""".strip()

PRICE_SIGNAL_SYSTEM = """
You are a Czech real estate price analyst.
Assess whether the asking price is fair.
Czech market context (2024-2025 asking prices per m²):
  - Prague: 80 000–150 000 CZK/m²
  - Brno: 50 000–90 000 CZK/m²
  - Regional cities (Znojmo, Třebíč): 20 000–45 000 CZK/m²
  - Villages / rural: 10 000–25 000 CZK/m²
  - Land (per m²): 500–5 000 CZK/m²

Signals:
  "low"  → price BELOW market value → buyer gets a good deal
  "fair" → price roughly aligned with actual value
  "high" → price ABOVE actual value → overpriced

Respond ONLY with valid JSON:
{"signal": "high", "reason": "Krátký důvod v češtině, max 250 znaků."}
signal must be exactly "low", "fair", or "high".
""".strip()

# ─── API helpers ──────────────────────────────────────────────────────────────

def fetch_listings_batch(job_type: str, batch_size: int) -> list[dict]:
    """Stáhne inzeráty, které potřebují daný AI job."""
    endpoint_map = {
        "normalize":    "/api/ollama/normalize-needed",
        "smart-tags":   "/api/ollama/smart-tags-needed",
        "price-signal": "/api/ollama/price-signal-needed",
    }
    endpoint = endpoint_map.get(job_type)
    if not endpoint:
        raise ValueError(f"Neznámý job_type: {job_type}")

    # Fallback: použij search endpoint se stránkováním
    r = httpx.get(
        f"{API_BASE_URL}/api/listings/search",
        json={"page": 1, "pageSize": batch_size},
        timeout=30,
    )
    r.raise_for_status()
    data = r.json()
    return data.get("items", [])


def update_listing(listing_id: str, payload: dict) -> bool:
    """Pošle výsledek AI analýzy zpět do API."""
    r = httpx.patch(
        f"{API_BASE_URL}/api/listings/{listing_id}/ai-data",
        json=payload,
        timeout=10,
    )
    return r.is_success


# ─── Batch job builders ───────────────────────────────────────────────────────

def build_normalize_requests(listings: list[dict]) -> list[dict]:
    requests = []
    for l in listings:
        if not l.get("description") or len(l.get("description", "")) < 100:
            continue
        desc = l["description"][:2500]
        requests.append({
            "custom_id": l["id"],
            "body": {
                "model": MISTRAL_MODEL,
                "messages": [
                    {"role": "system", "content": NORMALIZE_SYSTEM},
                    {"role": "user",   "content": f"Název: {l.get('title','')}\nLokalita: {l.get('locationText','')}\nPopis: {desc}"},
                ],
                "response_format": {"type": "json_object"},
                "temperature": 0.1,
                "max_tokens": 512,
            },
        })
    return requests


def build_smart_tags_requests(listings: list[dict]) -> list[dict]:
    requests = []
    for l in listings:
        if not l.get("description") or len(l.get("description", "")) < 50:
            continue
        desc = l["description"][:2000]
        requests.append({
            "custom_id": l["id"],
            "body": {
                "model": MISTRAL_MODEL,
                "messages": [
                    {"role": "system", "content": SMART_TAGS_SYSTEM},
                    {"role": "user",   "content": f"Název: {l.get('title','')}\nPopis: {desc}"},
                ],
                "response_format": {"type": "json_object"},
                "temperature": 0.1,
                "max_tokens": 128,
            },
        })
    return requests


def build_price_signal_requests(listings: list[dict]) -> list[dict]:
    requests = []
    for l in listings:
        price = l.get("price")
        if not price:
            continue
        area = l.get("areaBuiltUp") or l.get("areaLand")
        price_per_m2 = f"{price / area:.0f} Kč/m²" if area else "neznámé"
        requests.append({
            "custom_id": l["id"],
            "body": {
                "model": MISTRAL_MODEL,
                "messages": [
                    {"role": "system", "content": PRICE_SIGNAL_SYSTEM},
                    {"role": "user",   "content": (
                        f"Typ: {l.get('propertyType','?')} | Nabídka: {l.get('offerType','?')}\n"
                        f"Cena: {price:,.0f} Kč ({price_per_m2})\n"
                        f"Plocha: {area or 'neznámá'} m²\n"
                        f"Lokalita: {l.get('locationText','?')}\n"
                        f"Stav: {l.get('condition','neznámý')}\n"
                        f"Název: {l.get('title','')}"
                    )},
                ],
                "response_format": {"type": "json_object"},
                "temperature": 0.1,
                "max_tokens": 256,
            },
        })
    return requests


# ─── Batch runner ─────────────────────────────────────────────────────────────

def run_batch_job(job_type: str, listings: list[dict], client: Mistral) -> dict[str, Any]:
    """
    Spustí Mistral Batch job, počká na dokončení, vrátí výsledky.
    """
    builder_map = {
        "normalize":    build_normalize_requests,
        "smart-tags":   build_smart_tags_requests,
        "price-signal": build_price_signal_requests,
    }
    builder = builder_map[job_type]
    requests = builder(listings)
    if not requests:
        logger.info("Žádné záznamy ke zpracování pro job '%s'.", job_type)
        return {}

    logger.info("Spouštím Batch job '%s': %d požadavků (model %s)...", job_type, len(requests), MISTRAL_MODEL)

    job = client.batch.jobs.create(
        requests=requests,
        model=MISTRAL_MODEL,
        endpoint="/v1/chat/completions",
        metadata={"job_type": job_type, "source": "mistral_batch.py"},
    )
    logger.info("Job vytvořen: %s | Status: %s", job.id, job.status)

    # ── Polling ──────────────────────────────────────────────────────────────
    poll_interval = 15  # sekund
    while job.status not in ("SUCCESS", "FAILED", "TIMEOUT_EXCEEDED", "CANCELLED"):
        time.sleep(poll_interval)
        job = client.batch.jobs.get(job_id=job.id)
        logger.info("  … status: %s | hotovo: %s/%s",
                    job.status,
                    getattr(job, "total_requests", "?") - getattr(job, "requests_in_progress", 0),
                    getattr(job, "total_requests", "?"))

    if job.status != "SUCCESS":
        logger.error("Batch job selhal: status=%s", job.status)
        return {}

    logger.info("Batch job dokončen. Stahuju výsledky...")
    output_stream = client.files.download(file_id=job.output_file)
    results: dict[str, Any] = {}
    for line in output_stream.iter_lines():
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            custom_id = row.get("custom_id", "")
            body = row.get("response", {}).get("body", {})
            content = body.get("choices", [{}])[0].get("message", {}).get("content", "")
            if content:
                results[custom_id] = json.loads(content)
        except (json.JSONDecodeError, KeyError, IndexError) as e:
            logger.warning("Parse error pro řádek: %s – %s", line[:100], e)

    logger.info("Výsledků k zápisu: %d", len(results))
    return results


# ─── Write-back ───────────────────────────────────────────────────────────────

def writeback_normalize(results: dict[str, Any]) -> None:
    ok = err = 0
    for listing_id, data in results.items():
        payload = {"aiNormalizedData": json.dumps(data)}
        if update_listing(listing_id, payload):
            ok += 1
        else:
            err += 1
            logger.warning("Writeback selhal pro %s", listing_id)
    logger.info("Normalize writeback: %d ok, %d chyb", ok, err)


def writeback_smart_tags(results: dict[str, Any]) -> None:
    ok = err = 0
    for listing_id, data in results.items():
        # data může být list nebo {"tags": [...]}
        tags = data if isinstance(data, list) else data.get("tags", [])
        if not tags:
            continue
        payload = {"smartTags": json.dumps(tags[:5])}
        if update_listing(listing_id, payload):
            ok += 1
        else:
            err += 1
    logger.info("Smart tags writeback: %d ok, %d chyb", ok, err)


def writeback_price_signal(results: dict[str, Any]) -> None:
    ok = err = 0
    for listing_id, data in results.items():
        signal = data.get("signal", "")
        if signal not in ("low", "fair", "high"):
            continue
        payload = {
            "priceSignal": signal,
            "priceSignalReason": data.get("reason", "")[:500],
        }
        if update_listing(listing_id, payload):
            ok += 1
        else:
            err += 1
    logger.info("Price signal writeback: %d ok, %d chyb", ok, err)


# ─── Main ─────────────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(
        description="Mistral Batch Inference pro RealEstateAggregator AI joby"
    )
    parser.add_argument(
        "job",
        choices=["normalize", "smart-tags", "price-signal", "all"],
        help="Typ AI jobu ke spuštění",
    )
    parser.add_argument(
        "--batch-size", "-n",
        type=int, default=500,
        help="Počet inzerátů ke zpracování (default: 500)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Zobraz počet požadavků bez spuštění batch jobu",
    )
    args = parser.parse_args()

    if not MISTRAL_API_KEY:
        logger.error("MISTRAL_API_KEY není nastaven. Export: export MISTRAL_API_KEY=sk-...")
        sys.exit(1)

    client = Mistral(api_key=MISTRAL_API_KEY)

    jobs_to_run = (
        ["normalize", "smart-tags", "price-signal"]
        if args.job == "all"
        else [args.job]
    )

    for job_type in jobs_to_run:
        logger.info("═══ Job: %s (batch-size=%d) ═══", job_type, args.batch_size)

        # Stáhni inzeráty
        try:
            listings = fetch_listings_batch(job_type, args.batch_size)
        except Exception as e:
            logger.error("Fetch selhal: %s", e)
            continue

        logger.info("Staženo %d inzerátů.", len(listings))

        if args.dry_run:
            builder_map = {
                "normalize":    build_normalize_requests,
                "smart-tags":   build_smart_tags_requests,
                "price-signal": build_price_signal_requests,
            }
            reqs = builder_map[job_type](listings)
            logger.info("[dry-run] Počet požadavků: %d", len(reqs))
            continue

        # Spusť batch
        results = run_batch_job(job_type, listings, client)

        # Write-back do API
        writeback_map = {
            "normalize":    writeback_normalize,
            "smart-tags":   writeback_smart_tags,
            "price-signal": writeback_price_signal,
        }
        if results:
            writeback_map[job_type](results)


if __name__ == "__main__":
    main()
