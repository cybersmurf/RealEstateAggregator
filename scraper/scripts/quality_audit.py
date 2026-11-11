"""
Denní audit kvality scraperů (od 6. 11. 2026).

Proč: audit 6. 11. 2026 našel chyby, které běžely týdny bez povšimnutí – Reality Čechy každou noc
vypršel limit (od 15. 10.), Bazoš ukládal zahrady jako byty a pronájmy jako prodej, M&M měl u všech
inzerátů popis „Naše produkty“, smazané inzeráty (soft 404) zůstávaly aktivní, iDNES/Realingo měly
mrtvé fotky. Healthcheck i Prometheus byly zelené, protože zdroje pořád „něco“ ukládaly.

Audit měří po zdrojích:
  runs     – poslední běhy (selhání po sobě, stáří posledního úspěchu)
  stale    – podíl aktivních inzerátů, které běh neviděl 3+ dny
  fill     – vyplněnost polí proti mediánu posledních 7 denních snímků (propad = chyba parseru)
  same_desc– stejný popis u mnoha inzerátů (parser bere menu/šablonu)
  types    – titulek proti typu nemovitosti / nabídky (pozemek jako byt, pronájem jako prodej)
  live     – vzorek inzerátů proti živému webu: smazaný/404, cena na stránce, fotky jdou stáhnout
  shared   – stejná fotka u 3+ různých inzerátů (logo, placeholder, fotky z „podobných nabídek“)
  unit_price – pozemky s celkovou cenou pod 5 Kč/m² (cena za m² uložená jako celková)

Spuštění v kontejneru scraperu:
    docker exec realestate-scraper python scripts/quality_audit.py            # text
    docker exec realestate-scraper python scripts/quality_audit.py --json     # pro agenta
    volby: --no-live, --sample N (výchozí 4 inzeráty na zdroj), --source KOD
Exit code: 0 = vše v pořádku, 1 = jen varování, 2 = aspoň jedna chyba.
"""
import argparse
import asyncio
import html as htmllib
import json
import os
import random
import re
import statistics
import sys
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import quote

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import httpx

from core.database import init_db_manager

UA = ("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/130.0 Safari/537.36")

# Stejné značky jako DeadListingDetector v API – smazaný inzerát se stavem 200
GONE_MARKERS = (
    "smazany_inzerat",
    "Nemovitost již není v nabídce",
    "Požadovaný inzerát již není v naší databázi",
)
NUM_RE = re.compile(r"(?<!\d)(\d{1,3}(?:[   .]\d{3})+|\d+)(?!\d)")

FILL_METRICS = ("price", "area", "obec", "desc200", "photos")
FILL_DROP_PP = 15.0          # propad vyplněnosti o 15+ procentních bodů proti mediánu = chyba
STALE_RED_SHARE = 0.05       # 5 % aktivních neviděných 3+ dny
TYPE_MISMATCH_SHARE = 0.03
UNIT_PRICE_MAX_KC_M2 = 5      # celková cena pozemku pod 5 Kč/m² = jednotková cena uložená jako celková
UNIT_PRICE_MIN_COUNT = 3
UNIT_PRICE_NOTE_RE = re.compile(r"^\s*(\d[\d\s\u00a0]*)\s*Kč/m")
SNAPSHOT_DDL = """
CREATE TABLE IF NOT EXISTS re_realestate.scraper_quality_snapshots (
    source_code text NOT NULL,
    taken_on date NOT NULL,
    metrics jsonb NOT NULL,
    PRIMARY KEY (source_code, taken_on)
)"""


class Report:
    def __init__(self) -> None:
        self.sources: Dict[str, Dict[str, Any]] = defaultdict(lambda: {"issues": []})

    def add(self, src: str, check: str, level: str, message: str, examples: Optional[List[str]] = None) -> None:
        self.sources[src]["issues"].append(
            {"check": check, "level": level, "message": message, "examples": (examples or [])[:5]})

    def level(self, src: str) -> str:
        levels = {i["level"] for i in self.sources[src]["issues"]}
        return "red" if "red" in levels else "yellow" if "yellow" in levels else "ok"


async def check_runs(conn, report: Report, sources: List[str]) -> None:
    # Sreality zapisuje řádek za každou dvojici okres × kategorie (dražby pozemků mívají 0) –
    # běh zdroje = všechny řádky se stejným started_at, sečtené.
    rows = await conn.fetch("""
        WITH runs AS (
            SELECT source_code, started_at,
                   CASE WHEN bool_and(status = 'Succeeded') THEN 'Succeeded'
                        ELSE min(status) FILTER (WHERE status <> 'Succeeded') END AS status,
                   sum(coalesce(total_seen, 0)) AS total_seen,
                   left(coalesce(max(error_message), ''), 120) AS err
            FROM re_realestate.scrape_runs WHERE started_at > now() - interval '14 days'
            GROUP BY source_code, started_at)
        SELECT source_code, status, started_at, total_seen, err
        FROM (SELECT *, row_number() OVER (PARTITION BY source_code ORDER BY started_at DESC) rn FROM runs) x
        WHERE rn <= 5 ORDER BY source_code, started_at DESC""")
    by: Dict[str, List[Any]] = defaultdict(list)
    for r in rows:
        by[r["source_code"]].append(r)
    now = datetime.now(timezone.utc)
    for src in sources:
        runs = by.get(src, [])
        if not runs:
            report.add(src, "runs", "red", "žádný běh za 14 dní")
            continue
        fails = 0
        for r in runs:
            if r["status"] == "Succeeded":
                break
            fails += 1
        if fails >= 2:
            report.add(src, "runs", "red", f"{fails} posledních běhů selhalo: {runs[0]['err'] or runs[0]['status']}")
        elif fails == 1:
            report.add(src, "runs", "yellow", f"poslední běh selhal: {runs[0]['err'] or runs[0]['status']}")
        last_ok = next((r["started_at"] for r in runs if r["status"] == "Succeeded"), None)
        if last_ok is None or now - last_ok > timedelta(hours=36):
            report.add(src, "runs", "red", f"poslední úspěšný běh {last_ok:%d. %m. %H:%M}" if last_ok else "žádný úspěšný běh")
        if runs[0]["status"] == "Succeeded" and runs[0]["total_seen"] == 0:
            report.add(src, "runs", "red", "poslední běh nenašel žádný inzerát")


async def check_stale(conn, report: Report) -> None:
    rows = await conn.fetch("""
        SELECT source_code, count(*) n,
               count(*) FILTER (WHERE last_seen_at < now() - interval '3 days') stale,
               (array_agg(url ORDER BY last_seen_at) FILTER (WHERE last_seen_at < now() - interval '3 days'))[1:3] ex,
               min(last_seen_at) oldest
        FROM re_realestate.listings WHERE is_active GROUP BY 1""")
    for r in rows:
        if r["stale"] >= 5 and r["stale"] / r["n"] > STALE_RED_SHARE:
            report.add(r["source_code"], "stale", "red",
                       f"{r['stale']} z {r['n']} aktivních inzerátů běh neviděl 3+ dny (nejstarší {r['oldest']:%d. %m.}) – "
                       "zdroj nedobíhá nebo je nedeaktivuje", list(r["ex"] or []))


async def fill_metrics(conn) -> Dict[str, Dict[str, float]]:
    rows = await conn.fetch("""
        WITH ph AS (SELECT listing_id, count(*) n FROM re_realestate.listing_photos GROUP BY 1)
        SELECT l.source_code, count(*) n,
          100.0 * avg((price > 0)::int) price,
          100.0 * avg((area_built_up IS NOT NULL OR area_land IS NOT NULL)::int) area,
          100.0 * avg((coalesce(municipality, '') <> '')::int) obec,
          100.0 * avg((length(description) >= 200)::int) desc200,
          100.0 * avg((coalesce(ph.n, 0) > 0)::int) photos,
          100.0 * avg((latitude IS NOT NULL)::int) gps
        FROM re_realestate.listings l LEFT JOIN ph ON ph.listing_id = l.id
        WHERE l.is_active GROUP BY 1""")
    return {r["source_code"]: {k: round(float(r[k]), 1) for k in ("n", "price", "area", "obec", "desc200", "photos", "gps")}
            for r in rows}


async def check_fill(conn, report: Report, metrics: Dict[str, Dict[str, float]]) -> None:
    await conn.execute(SNAPSHOT_DDL)
    history = await conn.fetch("""
        SELECT source_code, metrics FROM re_realestate.scraper_quality_snapshots
        WHERE taken_on >= current_date - 7 AND taken_on < current_date""")
    past: Dict[str, List[Dict[str, float]]] = defaultdict(list)
    for h in history:
        past[h["source_code"]].append(json.loads(h["metrics"]) if isinstance(h["metrics"], str) else h["metrics"])
    for src, m in metrics.items():
        if m["n"] < 10 or len(past[src]) < 3:
            continue
        for key in FILL_METRICS:
            base = statistics.median(p.get(key, m[key]) for p in past[src])
            if base - m[key] >= FILL_DROP_PP:
                report.add(src, "fill", "red", f"vyplněnost „{key}“ spadla z {base:.0f} % na {m[key]:.0f} % – změnil se web?")
        base_n = statistics.median(p.get("n", m["n"]) for p in past[src])
        if base_n >= 20 and m["n"] < 0.6 * base_n:
            report.add(src, "fill", "red", f"aktivních inzerátů {m['n']:.0f}, medián týdne {base_n:.0f}")
    for src, m in metrics.items():
        await conn.execute("""
            INSERT INTO re_realestate.scraper_quality_snapshots (source_code, taken_on, metrics)
            VALUES ($1, current_date, $2::jsonb)
            ON CONFLICT (source_code, taken_on) DO UPDATE SET metrics = EXCLUDED.metrics""",
                           src, json.dumps(m))


async def check_same_description(conn, report: Report) -> None:
    # Byty jednoho developerského projektu mají legitimně stejný dlouhý popis; šablona parseru
    # je krátká („Naše produkty“, „Rezervováno“) nebo pokrývá velkou část zdroje.
    rows = await conn.fetch("""
        WITH t AS (SELECT source_code, count(*) n FROM re_realestate.listings WHERE is_active GROUP BY 1)
        SELECT d.source_code, left(d.description, 80) d, length(d.description) len, count(*) c,
               count(DISTINCT coalesce(nullif(d.municipality, ''), d.title)) obce, (array_agg(d.url))[1:2] ex, t.n
        FROM re_realestate.listings d JOIN t USING (source_code)
        WHERE d.is_active AND d.description <> ''
        GROUP BY d.source_code, d.description, t.n HAVING count(*) >= 5""")
    for r in rows:
        if r["c"] / r["n"] >= 0.2:
            report.add(r["source_code"], "same_desc", "red",
                       f"{r['c']} z {r['n']} inzerátů má stejný popis „{r['d']}“ – parser bere šablonu, ne text", list(r["ex"]))
        elif r["len"] < 300 or r["obce"] >= 4:
            report.add(r["source_code"], "same_desc", "yellow",
                       f"{r['c']} inzerátů ({r['obce']} obcí) má stejný popis „{r['d']}“ ({r['len']} znaků) – šablona místo textu?",
                       list(r["ex"]))


async def check_types(conn, report: Report) -> None:
    rows = await conn.fetch(r"""
        SELECT source_code, count(*) n, array_remove(array_agg(CASE WHEN bad THEN url END), NULL) bad
        FROM (SELECT source_code, url,
                (offer_type = 'Sale' AND title ~* '(^|\W)(pronájem|pronajem|podnájem)')
                OR (offer_type = 'Rent' AND title ~* '^\s*prodej')
                OR (property_type = 'Apartment' AND title ~* '(rodinn[ýé]ho? d[ůo]m|zahrad[ay]\s+\d|^\s*(prodej\s+)?(stavební\s+)?pozem)')
                OR (property_type IN ('House', 'Apartment') AND title ~* '^\s*(prodej\s+)?(stavební\s+)?pozem')
                AS bad
              FROM re_realestate.listings WHERE is_active) x
        GROUP BY 1""")
    for r in rows:
        bad = list(r["bad"] or [])
        if r["n"] >= 20 and len(bad) / r["n"] >= TYPE_MISMATCH_SHARE:
            report.add(r["source_code"], "types", "yellow",
                       f"{len(bad)} z {r['n']} inzerátů má titulek v rozporu s typem nemovitosti/nabídky", bad)


async def check_unit_price(conn, report: Report) -> None:
    # 10. 11. 2026: Sreality, RealityMIX a Reality Čechy ukládaly cenu pozemku „za m²“ jako celkovou
    # (pole 19 297 m² za 55 Kč, 230+ aktivních pozemků) a žádná kontrola si toho nevšimla. I orná půda
    # na jižní Moravě stojí desítky Kč/m², celková cena pod 5 Kč/m² je jednotková cena.
    # Bazoš (od 11. 11. 2026 v kontrole): cenu za m² tam do pole ceny píše sám prodávající, scraper ji
    # pozná z popisu („Cena: 1 400 Kč/m2“) nebo podle ceny pod 5 Kč/m². Spoluvlastnický podíl má cenu
    # za podíl a výměru celého pozemku („Pole 4 729 m², Podíl 8 % o výměře 394 m²“) – to chyba není.
    rows = await conn.fetch("""
        SELECT source_code, count(*) c, (array_agg(url ORDER BY price / area_land))[1:5] ex
        FROM re_realestate.listings
        WHERE is_active AND property_type = 'Land' AND offer_type = 'Sale' AND price > 0 AND area_land > 0
          AND price / area_land < $1 AND title !~* 'pod[ií]l|spoluvlastn'
        GROUP BY 1 HAVING count(*) >= $2""", UNIT_PRICE_MAX_KC_M2, UNIT_PRICE_MIN_COUNT)
    for r in rows:
        report.add(r["source_code"], "unit_price", "yellow",
                   f"{r['c']} pozemků má celkovou cenu pod {UNIT_PRICE_MAX_KC_M2} Kč/m² – cena za m² uložená jako celková?",
                   list(r["ex"]))


async def check_shared_photos(conn, report: Report) -> None:
    rows = await conn.fetch("""
        SELECT l.source_code, p.original_url, count(DISTINCT l.id) c
        FROM re_realestate.listing_photos p JOIN re_realestate.listings l ON l.id = p.listing_id
        WHERE l.is_active GROUP BY 1, 2 HAVING count(DISTINCT l.id) >= 3""")
    by: Dict[str, List[Any]] = defaultdict(list)
    for r in rows:
        by[r["source_code"]].append(r)
    for src, items in by.items():
        items.sort(key=lambda r: -r["c"])
        report.add(src, "shared", "yellow",
                   f"{len(items)} fotek je u 3+ různých inzerátů (max {items[0]['c']}×) – logo, placeholder nebo cizí fotky",
                   [i["original_url"] for i in items])


def _numbers(text: str) -> set:
    out = set()
    for m in NUM_RE.finditer(text):
        try:
            out.add(int(re.sub(r"[   .]", "", m.group(1))))
        except ValueError:
            pass
    return out


async def _live_one(client: httpx.AsyncClient, item: Dict[str, Any]) -> Dict[str, Any]:
    res: Dict[str, Any] = {"url": item["url"], "dead": False, "price_ok": None, "photos_bad": 0, "photos_checked": 0}
    try:
        r = await client.get(item["url"])
    except Exception as exc:  # noqa: BLE001 – nedostupnost webu hlásí jiné kontroly
        res["error"] = type(exc).__name__
        return res
    final = str(r.url)
    body = r.text[:600_000] if r.status_code == 200 else ""
    eid = item.get("external_id") or ""
    if r.status_code in (404, 410):
        res["dead"] = True
    elif r.status_code == 200 and (
            any(m.lower() in body.lower() for m in GONE_MARKERS)
            or (len(eid) >= 4 and eid in item["url"] and eid not in final)):
        res["dead"] = True
    if r.status_code == 200 and not res["dead"] and item.get("price"):
        raw = htmllib.unescape(body)
        res["price_ok"] = int(float(item["price"])) in _numbers(raw)
        # Rezervovaná nabídka bez ceny na webu si záměrně drží poslední známou cenu (keep_last_price,
        # price_note „Rezervace“) – Premia Reality ukazuje u rezervací jen štítek a má jich přes polovinu.
        if not res["price_ok"] and (item.get("price_note") or "").lower().startswith("rezerv"):
            res["price_ok"] = None
        # Cena za m² (od 10. 11. 2026): ukládáme celkovou (jednotková × plocha), web ukazuje jednotkovou
        unit = UNIT_PRICE_NOTE_RE.match(item.get("price_note") or "")
        if not res["price_ok"] and unit:
            res["price_ok"] = int(re.sub(r"\D", "", unit.group(1))) in _numbers(raw)
    photos = item.get("photos") or []
    if photos and not res["dead"]:
        picks = [photos[0]] + ([random.choice(photos[1:])] if len(photos) > 1 else [])
        for p in picks:
            res["photos_checked"] += 1
            try:
                pr = await client.get(p, headers={"Referer": quote(item["url"], safe=":/?&=%#")})
                if pr.status_code != 200 or "image" not in pr.headers.get("content-type", "") or len(pr.content) < 3000:
                    res["photos_bad"] += 1
            except Exception:  # noqa: BLE001
                res["photos_bad"] += 1
            await asyncio.sleep(0.5)
    return res


async def check_live(conn, report: Report, sample: int, only: Optional[str]) -> None:
    rows = await conn.fetch("""
        SELECT * FROM (
          SELECT l.source_code, l.url, l.external_id, l.price, l.price_note,
                 row_number() OVER (PARTITION BY l.source_code
                                    ORDER BY (l.property_type = 'House' AND l.offer_type = 'Sale') DESC, random()) rn,
                 (SELECT array_agg(original_url ORDER BY order_index) FROM re_realestate.listing_photos p
                   WHERE p.listing_id = l.id) photos
          FROM re_realestate.listings l WHERE l.is_active AND ($2::text IS NULL OR l.source_code = $2)) x
        WHERE rn <= $1""", sample, only)
    by: Dict[str, List[Dict[str, Any]]] = defaultdict(list)
    for r in rows:
        by[r["source_code"]].append(dict(r))

    async with httpx.AsyncClient(headers={"User-Agent": UA, "Accept-Language": "cs-CZ,cs;q=0.9"},
                                 follow_redirects=True, timeout=25) as client:
        async def per_source(src: str, items: List[Dict[str, Any]]) -> None:
            results = []
            for it in items:
                results.append(await _live_one(client, it))
                await asyncio.sleep(1.0)
            dead = [r["url"] for r in results if r["dead"]]
            if len(dead) >= 2 or (dead and len(results) <= 2):
                report.add(src, "live", "red", f"{len(dead)} z {len(results)} vzorových aktivních inzerátů je na webu smazaných", dead)
            elif dead:
                report.add(src, "live", "yellow", f"1 z {len(results)} vzorových aktivních inzerátů je na webu smazaný", dead)
            bad_price = [r["url"] for r in results if r["price_ok"] is False]
            if len(bad_price) >= 2:
                report.add(src, "live", "yellow", f"u {len(bad_price)} z {len(results)} inzerátů není naše cena na stránce", bad_price)
            checked = sum(r["photos_checked"] for r in results)
            bad = sum(r["photos_bad"] for r in results)
            if checked and bad / checked >= 0.4 and bad >= 2:
                report.add(src, "live", "red", f"{bad} z {checked} kontrolovaných fotek nejde stáhnout – zastaralé galerie?",
                           [r["url"] for r in results if r["photos_bad"]])
            errors = [r for r in results if r.get("error")]
            if len(errors) == len(results) and results:
                report.add(src, "live", "yellow", f"web neodpovídá ({errors[0]['error']})")

        await asyncio.gather(*(per_source(s, i) for s, i in by.items()))


async def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--no-live", action="store_true")
    ap.add_argument("--sample", type=int, default=4)
    ap.add_argument("--source")
    args = ap.parse_args()

    db = init_db_manager(
        host=os.getenv("DB_HOST", "localhost"), port=int(os.getenv("DB_PORT", "5432")),
        database=os.getenv("DB_NAME", "realestate_dev"), user=os.getenv("DB_USER", "postgres"),
        password=os.getenv("DB_PASSWORD", "dev"), min_size=1, max_size=3)
    await db.connect()
    report = Report()
    async with db.acquire() as conn:
        sources = [r["code"] for r in await conn.fetch(
            "SELECT code FROM re_realestate.sources WHERE is_active ORDER BY code")]
        if args.source:
            sources = [s for s in sources if s == args.source]
        await check_runs(conn, report, sources)
        await check_stale(conn, report)
        metrics = await fill_metrics(conn)
        await check_fill(conn, report, metrics)
        await check_same_description(conn, report)
        await check_types(conn, report)
        await check_unit_price(conn, report)
        await check_shared_photos(conn, report)
        if not args.no_live:
            await check_live(conn, report, args.sample, args.source)

    out = {
        "generated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "sources": {s: {"level": report.level(s), "active": int(metrics.get(s, {}).get("n", 0)),
                        "fill": metrics.get(s, {}), "issues": report.sources[s]["issues"]}
                    for s in sources},
    }
    levels = [v["level"] for v in out["sources"].values()]
    out["level"] = "red" if "red" in levels else "yellow" if "yellow" in levels else "ok"

    if args.json:
        print(json.dumps(out, ensure_ascii=False, indent=1, default=str))
    else:
        icon = {"red": "🔴", "yellow": "🟡", "ok": "🟢"}
        print(f"Audit kvality scraperů {date.today():%d. %m. %Y} – celkově {icon[out['level']]}")
        for src, v in out["sources"].items():
            print(f"{icon[v['level']]} {src} ({v['active']} aktivních)")
            for i in v["issues"]:
                print(f"    [{i['check']}] {i['message']}")
                for e in i["examples"][:2]:
                    print(f"        {e}")
    await db.close() if hasattr(db, "close") else None
    return {"ok": 0, "yellow": 1, "red": 2}[out["level"]]


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
