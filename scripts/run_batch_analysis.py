#!/usr/bin/env python3
"""
Batch analysis script – spustí mistral-tools/mistral-large-latest analýzu
pro všechny inzeráty v kategoriích Liked/ToVisit/Visited které ji ještě nemají.
"""
import httpx
import time
import sys
from datetime import datetime

API_BASE = "http://localhost:5001"
MODEL = "mistral-tools/mistral-large-latest"
TIMEOUT = 360  # 6 minut timeout na jednu analýzu

# Všechny inzeráty z kategorií Liked/ToVisit/Visited
# Citonice (56acfe) a Suchohrdly (14fe11) MAJÍ mistral-tools → přeskočí se
LISTINGS = [
    # Liked
    ("7b5b50ac-6a7d-4e58-bf4b-d3846ea5e6cf", "Těšetice u Znojma",   "3.49M"),
    ("b410bb3f-a2f8-41d4-a47b-692045a9bba0", "Jaroslavice",          "3.69M"),
    ("383c843d-3b15-4113-b75f-1185b933d5d2", "Slatina",              "3.75M"),
    ("ac332897-a5aa-4ed8-875b-485928ff2f0a", "Vedrovice",            "6.19M"),
    ("e4328a6c-268c-4289-8dba-381157b876fa", "Suchohrdly vila",      "6.57M"),
    ("57a4033e-5eb3-404a-9898-a2737de990b7", "Znojmo",               "6.70M"),
    ("344daecc-8b1a-4057-93a7-9fe2b14c24cc", "Hrabětice",            "6.74M"),
    ("6cb00624-e930-42c6-8159-7f18f9afade9", "Dobšice",              "7.35M"),
    # ToVisit (Citonice + Kuchařovice-Ke-Kapličce mají analýzu, ale ne mistral-tools)
    ("56acfea4-0c04-44c3-8ea8-b0e5c8e1d250", "Citonice",             "4.20M"),  # HAS mistral-tools
    ("45cd728f-13e5-4bab-9397-fcdc22cfd318", "Kuchařovice (Ke Kapličce)", "4.39M"),
    ("b08f88b7-a317-420c-a0d3-f0c3132ee813", "Křídlůvky",            "6.49M"),
    ("ae5745e9-b580-43a7-b262-420de5eac3f7", "Práče",                "7.30M"),
    ("e9ea13a5-77f4-42b7-a540-8a4aee968371", "Kuchařovice (Znojemská)", "7.45M"),
    # Visited
    ("74b6f591-78ff-4222-9450-9535d5e2639e", "Šumná",                "2.84M"),
    ("0de2b4e6-5e83-4128-8eb4-51c74a4776c8", "Leska Horní Znojmo",   "6.49M"),
    ("967a2865-0eb6-4642-bca9-87fc8e0ff4b8", "Štítary",              "6.90M"),
    ("14fe1165-c84f-4dcd-b5aa-ca01ae563f22", "Suchohrdly",           "6.99M"),  # HAS mistral-tools
]

# Ty co MAJÍ mistral-tools → přeskočit
SKIP_IDS = {
    "56acfea4-0c04-44c3-8ea8-b0e5c8e1d250",  # Citonice – 35 analýz
    "14fe1165-c84f-4dcd-b5aa-ca01ae563f22",  # Suchohrdly – 23 analýz
}


def log(msg: str) -> None:
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def analyze(listing_id: str, name: str, price: str) -> dict:
    url = f"{API_BASE}/api/listings/{listing_id}/analyze-local"
    params = {"model": MODEL}
    log(f"▶ START  {name} ({price}) …")
    start = time.time()
    try:
        resp = httpx.post(url, params=params, timeout=TIMEOUT)
        elapsed = time.time() - start
        if resp.status_code == 200:
            data = resp.json()
            length = len(data.get("analysis", "") or data.get("content", "") or str(data))
            log(f"✅ OK    {name} — HTTP {resp.status_code} · {elapsed:.0f}s · {length:,} znaků")
            return {"id": listing_id, "name": name, "price": price, "ok": True,
                    "elapsed": elapsed, "length": length}
        else:
            log(f"❌ FAIL  {name} — HTTP {resp.status_code}: {resp.text[:200]}")
            return {"id": listing_id, "name": name, "price": price, "ok": False,
                    "elapsed": elapsed, "error": f"HTTP {resp.status_code}"}
    except Exception as e:
        elapsed = time.time() - start
        log(f"❌ ERR   {name} — {e}")
        return {"id": listing_id, "name": name, "price": price, "ok": False,
                "elapsed": elapsed, "error": str(e)}


def main():
    to_run = [(lid, name, price) for lid, name, price in LISTINGS if lid not in SKIP_IDS]
    total = len(to_run)
    results = []

    log(f"=== Batch analýza {total} inzerátů ({MODEL}) ===")
    log(f"Přeskakuji: {len(SKIP_IDS)} (Citonice, Suchohrdly – mají mistral-tools)")
    print()

    for idx, (lid, name, price) in enumerate(to_run, 1):
        log(f"[{idx}/{total}] Zpracovávám: {name}")
        result = analyze(lid, name, price)
        results.append(result)
        print()

    # Souhrn
    ok = [r for r in results if r["ok"]]
    fail = [r for r in results if not r["ok"]]
    total_time = sum(r["elapsed"] for r in results)

    log("=" * 60)
    log(f"HOTOVO: {len(ok)}/{total} úspěšných   {len(fail)} selhání")
    log(f"Celkový čas: {total_time/60:.1f} min")
    if fail:
        log("Selhaly:")
        for r in fail:
            log(f"  ❌ {r['name']}: {r.get('error','?')}")

    return 0 if not fail else 1


if __name__ == "__main__":
    sys.exit(main())
