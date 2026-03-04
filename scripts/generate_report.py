#!/usr/bin/env python3
"""Generuje HTML srovnávací report ze všech analýz v DB."""
import subprocess, re, json, sys, unicodedata
from datetime import datetime
from pathlib import Path

LISTING_ID = "56acfea4-0c04-44c3-8ea8-b0e5c8e1d250"
OUTPUT = Path("/Users/petrsramek/Projects/RealEstateAggregator/exports/model-comparison-2026-03-04-v4.html")

def psql(sql):
    r = subprocess.run(
        ["docker", "exec", "realestate-db", "psql",
         "-U", "postgres", "-d", "realestate_dev", "-t", "-A", "-c", sql],
        capture_output=True, text=True)
    return r.stdout.strip()

def strip_diacritics(text: str) -> str:
    """Odstraní háčky a čárky pro porovnávání ASCII regex patternů s českou diakritikou."""
    return ''.join(
        c for c in unicodedata.normalize('NFD', text)
        if unicodedata.category(c) != 'Mn'
    )

# ── Modely k porovnání ─────────────────────────────────────────────────────
MODELS = [
    ("Claude Opus 4.6",      "local:claude/claude-opus-4-6",               "cloud", "#4f46e5"),
    ("Claude Sonnet 4.6",    "local:claude/claude-sonnet-4-6",              "cloud", "#7c3aed"),
    ("Claude Sonnet 4.5",    "local:claude/claude-sonnet-4-5-20250929",     "cloud", "#6d28d9"),
    ("Claude 3.5 (MCP)",     "claude",                                       "cloud", "#a855f7"),
    ("Claude 3 Haiku",       "local:claude/claude-3-haiku-20240307",        "cloud", "#c084fc"),
    ("Mistral Large",        "local:mistral/mistral-large-latest",           "cloud", "#f97316"),
    ("DeepSeek V3.1 671B",   "local:ollama-cloud/deepseek-v3.1:671b",       "cloud", "#0891b2"),
    ("Gemma3 27B",           "local:ollama-cloud/gemma3:27b",                "cloud", "#0d9488"),
    ("Mistral S3.2 24B",     "local:mistral-small3.2:24b",                  "local", "#eab308"),
    ("Groq Llama3.3 70B",    "local:groq/llama-3.3-70b-versatile",          "cloud", "#dc2626"),
    ("Groq + Tools",         "local:groq-tools/llama-3.3-70b-versatile",    "cloud", "#b91c1c"),
    ("Qwen2.5 14B",          "qwen-local",                                   "local", "#16a34a"),
    ("Qwen3.5 9B",           "local:qwen3.5:9b",                            "local", "#6b7280"),
]

# ── Načti texty + časy z DB (dva oddělené dotazy kvůli | v markdown textech) ─
entries = []
for name, src, kind, color in MODELS:
    text = psql(
        f"SELECT content FROM re_realestate.listing_analyses "
        f"WHERE listing_id='{LISTING_ID}' AND source='{src}' "
        f"ORDER BY created_at DESC LIMIT 1"
    )
    if not text:
        print(f"Preskakuji {name} - analyza v DB nenalezena", file=sys.stderr)
        continue
    elapsed_raw = psql(
        f"SELECT coalesce(elapsed_seconds::text,'') "
        f"FROM re_realestate.listing_analyses "
        f"WHERE listing_id='{LISTING_ID}' AND source='{src}' "
        f"ORDER BY created_at DESC LIMIT 1"
    ).strip()
    if elapsed_raw and elapsed_raw not in ("", "None", "NULL"):
        try:
            elapsed = f"{float(elapsed_raw):.0f}s"
        except ValueError:
            elapsed = "n/a"
    else:
        elapsed = "n/a"
    entries.append({"name": name, "src": src, "kind": kind, "color": color,
                    "elapsed": elapsed, "text": text})

# ── Témata ─────────────────────────────────────────────────────────────────
TOPICS = [
    ("Odlozene predani 2027",            r"predani.*2027|2027.*predani|brezen.*2027|predani.{0,30}rok"),
    ("Stodola / hospodarska vada",       r"stodol|hospodar"),
    ("Rozbor studnicni vody",            r"laboratorni|rozbor.*vod|studnicni.*vod|voda.{0,30}analyz"),
    ("Smluvni pokuta za prodleni",       r"smluvni.{0,5}pokut|pokuta.{0,30}prodleni"),
    ("Rohova parcela",                   r"rohov.{0,10}parcel|rohov.{0,10}pozemek"),
    ("Hodnota po rekonstrukci",          r"hodnota.{0,20}po rekonstrukci|po rekonstrukci.{0,30}\d"),
    ("Yield / hruby vynos",              r"yield|hrub.{0,10}vynos|hrub.{0,10}vyno|odhadovany najem|rocni najem"),
    ("Bodovaci tabulka / skore",         r"\d\s*/\s*5\b|SKORE|HODNOCENI.*bod|celkov.{0,10}hodnoceni"),
    ("ROI / investicni navratnost",      r"navratnost|ROI|return"),
    ("Otazky pred koupi / DD",           r"otazky|co.{0,10}zeptat|Due Diligence|co poverit|doporucujeme"),
    ("Konkretni vady (bullets)",         r"rizika|VADY|PROBLEMY"),
    ("Max nabidkova cena (Kc)",          r"[3-4]\s*[0-9]{2,3}\s*[0-9]{3}|maximalni.*cena|nabidnout"),
    ("PENB chybejici",                   r"PENB.{0,30}chybi|chybi.{0,30}PENB|PENB.*kriti|energetick.{0,20}prukaz"),
    ("Povodnove riziko",                 r"povodnov|povoden|zaplaveni"),
    ("Srovnani Kc/m2 s trhem",           r"trzni.{0,50}Kc|trzne.{0,50}Kc|prumer.{0,20}Kc/m|trzni.{0,30}obvyklou|trzne.{0,30}obvyklou"),
    # ─ Kvalita cestiny ──────────────────────────────────────────────────────
    ("Diakritika > 3 %",                 None),   # pocitano pres czech_score()
    ("Ceska slovni zasoba",              None),
    ("Neodpovida anglicky",              None),
]

TOPIC_LABELS = [
    "🗓 Odložené předání 2027",
    "🏚 Stodola / hospodářská vada",
    "💧 Rozbor studniční vody",
    "⚖ Smluvní pokuta za prodlení",
    "📐 Rohová parcela",
    "💰 Hodnota po rekonstrukci",
    "📊 Yield / hrubý výnos",
    "🏆 Bodovací tabulka / skóre",
    "📈 ROI / investiční návratnost",
    "❓ Otázky před koupí / DD",
    "⚠ Konkrétní vady (bullets)",
    "🏠 Max nabídková cena (Kč)",
    "🔴 PENB chybějící",
    "🌊 Povodňové riziko",
    "📌 Srovnání Kč/m² s trhem",
    "🇨🇿 Diakritika > 3 %",
    "🗣 Česká slovní zásoba",
    "🚫 Neodpovídá anglicky",
]

# ── Pomocne funkce pro cestinu ──────────────────────────────────────────────
CZ_DIACRITICS = set("acdeeeinorstuuyzACDEEINORSTUUYZ")  # ASCII approx doesn't work, use Unicode
CZ_DIACRITICS = set("áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ")
CZ_REALTY_WORDS = [
    "nemovitost", "inzerat", "prodej", "koupe", "cena", "plocha", "pozemek",
    "rekonstrukc", "stav", "lokac", "dispozic", "mistnost", "koupeln",
    "zahrad", "sklep", "garaz", "vytapeni", "doporucuj",
    "nemovitosti", "inzerátu", "prodeje", "koupě", "ceny", "plochy",
    "rekonstrukce", "koupelna", "zahrada", "garáž", "vytápění", "doporučuj",
]
EN_SIGNAL = re.compile(
    r"\b(the property|this house|would |however,|furthermore|in conclusion|overall,|I recommend)",
    re.IGNORECASE
)

def czech_score(text):
    """Vraci tuple (diakritika_ok, cz_slovnik_ok, ne_anglicky)."""
    alpha = [c for c in text if c.isalpha()]
    cz_ratio = sum(1 for c in alpha if c in CZ_DIACRITICS) / max(len(alpha), 1)
    cz_words  = sum(1 for w in CZ_REALTY_WORDS if w.lower() in text.lower())
    return (cz_ratio > 0.03, cz_words >= 4, not bool(EN_SIGNAL.search(text)))

# ── Spocitej skore ─────────────────────────────────────────────────────────
for e in entries:
    cs = czech_score(e["text"])
    e["czech"] = cs
    # strip_diacritics: ASCII regex patterny (predani, povodnov...) musí matchovat i české háčky
    text_norm = strip_diacritics(e["text"])
    e["score"] = (
        sum(1 for _, pat in TOPICS if pat is not None
            and re.search(pat, text_norm, re.IGNORECASE))
        + sum(cs)
    )
    e["topics"] = [
        (re.search(pat, text_norm, re.IGNORECASE) is not None) if pat is not None else csval
        for (_, pat), csval in zip(TOPICS, [None] * 15 + list(cs))
    ]

max_score = max(e["score"] for e in entries)

# ── Markdown to HTML (simple) ───────────────────────────────────────────
def md_to_html(text):
    import html as html_lib
    lines = text.split("\n")
    out = []
    in_table = False
    in_ul = False
    for line in lines:
        line_e = html_lib.escape(line)
        if line_e.startswith("|") and "|" in line_e[1:]:
            if not in_table:
                if in_ul: out.append("</ul>"); in_ul = False
                out.append('<table class="md-table">')
                in_table = True
            cells = [c.strip() for c in line_e.strip("|").split("|")]
            if all(re.match(r'^[-: ]+$', c) for c in cells if c):
                continue
            tag = "th" if not in_table else "td"
            out.append("<tr>" + "".join(f"<{tag}>{c}</{tag}>" for c in cells) + "</tr>")
            continue
        else:
            if in_table: out.append("</table>"); in_table = False
        if line_e.startswith("### "):
            if in_ul: out.append("</ul>"); in_ul = False
            out.append(f"<h3>{line_e[4:]}</h3>")
        elif line_e.startswith("## "):
            if in_ul: out.append("</ul>"); in_ul = False
            out.append(f"<h2>{line_e[3:]}</h2>")
        elif line_e.startswith("# "):
            if in_ul: out.append("</ul>"); in_ul = False
            out.append(f"<h1>{line_e[2:]}</h1>")
        elif line_e.startswith("- ") or line_e.startswith("* "):
            if not in_ul: out.append("<ul>"); in_ul = True
            item = line_e[2:]
            item = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', item)
            out.append(f"<li>{item}</li>")
        elif line_e.strip() in ("", "---"):
            if in_ul: out.append("</ul>"); in_ul = False
            out.append("<hr>" if line_e.strip() == "---" else "<br>")
        else:
            if in_ul: out.append("</ul>"); in_ul = False
            line_e = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', line_e)
            line_e = re.sub(r'\*(.+?)\*', r'<em>\1</em>', line_e)
            out.append(f"<p>{line_e}</p>")
    if in_table: out.append("</table>")
    if in_ul: out.append("</ul>")
    return "\n".join(out)

# ── Generuj HTML ───────────────────────────────────────────────────────────
tabs_html = "\n".join(
    f'<button class="tab-btn" onclick="showTab({i})">{e["name"]}</button>'
    for i, e in enumerate(entries)
)
panels_html = "\n".join(
    f'<div class="tab-panel" id="panel-{i}" style="display:none">{md_to_html(e["text"])}</div>'
    for i, e in enumerate(entries)
)

# Scoreboard cards
cards_html = ""
sorted_entries = sorted(entries, key=lambda x: x["score"], reverse=True)
for rank, e in enumerate(sorted_entries, 1):
    pct = e["score"] / len(TOPICS) * 100
    crown = "WINNER " if rank == 1 else ""
    note  = "LONGEST " if len(e["text"]) == max(len(x["text"]) for x in entries) else ""
    kind_badge = f'<span class="badge badge-{e["kind"]}">{e["kind"].upper()}</span>'
    cz = e.get("czech", (False, False, False))
    cz_flags = (
        ('<span class="cz ok" title="Diakritika OK">CZ</span>' if cz[0] else '<span class="cz fail" title="Malo diakritiky">!D</span>') +
        (' <span class="cz ok" title="Ceska slovni zasoba OK">SL</span>' if cz[1] else ' <span class="cz fail" title="Chybi ceska slova">!S</span>') +
        (' <span class="cz ok" title="Nepise anglicky">OK</span>' if cz[2] else ' <span class="cz fail" title="Detekovana anglictina">EN</span>')
    )
    cards_html += f"""
    <div class="card" style="border-left: 4px solid {e['color']}">
      <div class="card-header">
        <span class="rank">#{rank}</span>
        <span class="model-name">{crown}{e['name']}{note}</span>
        {kind_badge}
        <span class="elapsed-badge">time: {e['elapsed']}</span>
        <div class="cz-row">{cz_flags}</div>
      </div>
      <div class="score-bar-wrap">
        <div class="score-bar" style="width:{pct:.0f}%; background:{e['color']}"></div>
      </div>
      <span class="score-val">{e['score']}/{len(TOPICS)}</span>
      <span class="char-count">{len(e['text']):,} znaku</span>
    </div>"""

# Topic matrix
matrix_header = "<tr><th>Tema</th>" + "".join(f'<th title="{e["name"]}">{e["name"][:10]}</th>' for e in entries) + "</tr>"
matrix_rows = ""
for t_idx, tlabel in enumerate(TOPIC_LABELS):
    row = f"<tr><td>{tlabel}</td>"
    for e in entries:
        ok = e["topics"][t_idx]
        row += f'<td class="{"hit" if ok else "miss"}">{"OK" if ok else "-"}</td>'
    row += "</tr>"
    matrix_rows += row

now = datetime.now().strftime("%d. %m. %Y %H:%M")

html = """<!DOCTYPE html>
<html lang="cs">
<head>
<meta charset="UTF-8">
<title>Model Benchmark """ + now + """</title>
<style>
  body{font-family:system-ui,sans-serif;background:#0f172a;color:#e2e8f0;margin:0;padding:2rem}
  h1{color:#f1f5f9;font-size:1.6rem;margin-bottom:.2rem}
  .sub{color:#94a3b8;font-size:.9rem;margin-bottom:2rem}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:1rem;margin-bottom:2rem}
  .card{background:#1e293b;border-radius:.75rem;padding:1rem}
  .card-header{display:flex;align-items:center;flex-wrap:wrap;gap:.4rem;margin-bottom:.6rem}
  .rank{color:#94a3b8;font-size:.85rem;min-width:2rem}
  .model-name{font-weight:700;font-size:1rem}
  .badge{padding:.15rem .45rem;border-radius:.25rem;font-size:.7rem;font-weight:700}
  .badge-cloud{background:#1d4ed8;color:#bfdbfe}
  .badge-local{background:#166534;color:#bbf7d0}
  .elapsed-badge{font-size:.75rem;color:#94a3b8;margin-left:auto}
  .cz-row{width:100%;font-size:.8rem;letter-spacing:.1rem;margin-top:.2rem}
  .cz.ok{color:#4ade80;font-weight:700}
  .cz.fail{color:#f87171;opacity:.7}
  .score-bar-wrap{background:#334155;border-radius:4px;height:8px;margin:.4rem 0}
  .score-bar{height:8px;border-radius:4px}
  .score-val{font-size:1.3rem;font-weight:700}
  .char-count{font-size:.75rem;color:#94a3b8;margin-left:.8rem}
  .matrix{width:100%;border-collapse:collapse;font-size:.8rem;margin-bottom:2rem;overflow-x:auto;display:block}
  .matrix th,.matrix td{padding:.3rem .5rem;border:1px solid #334155;white-space:nowrap}
  .matrix th{background:#1e293b;color:#94a3b8}
  .hit{background:#052e16;color:#4ade80;text-align:center}
  .miss{background:#1a0a0a;color:#f87171;text-align:center}
  .md-table{border-collapse:collapse;width:100%;margin:.5rem 0}
  .md-table th,.md-table td{border:1px solid #334155;padding:.3rem .6rem;text-align:left}
  .md-table th{background:#1e293b}
  .tabs{display:flex;flex-wrap:wrap;gap:.4rem;margin-bottom:1rem}
  .tab-btn{background:#1e293b;border:1px solid #334155;color:#94a3b8;padding:.4rem .9rem;border-radius:.4rem;cursor:pointer;font-size:.85rem}
  .tab-btn:hover{background:#334155;color:#f1f5f9}
  .tab-panel h1,.tab-panel h2,.tab-panel h3{color:#93c5fd}
  .tab-panel p{line-height:1.6;color:#cbd5e1}
  .tab-panel ul{padding-left:1.2rem;color:#cbd5e1}
  .note{background:#1e3a5f;border-left:3px solid #3b82f6;padding:.7rem 1rem;border-radius:0 .5rem .5rem 0;margin-bottom:1.5rem;font-size:.85rem;color:#93c5fd}
</style>
</head>
<body>
<h1>Model Benchmark - Citonice 5+1, 240 m2</h1>
<div class="sub">Generovano """ + now + """ - """ + str(len(entries)) + """ modelu - """ + str(len(TOPICS)) + """ hodnoticich temat (15 obsah + 3 cestina)</div>

<div class="note">
  POZNAMKA: Claude 3.5 (MCP) je analyza z <strong>Claude Desktop s MCP tools</strong> - model sam volal get_listing(), cetl fotky a data iterativne.
  Ostatni modely dostaly <strong>jednorazovy textovy prompt</strong> bez pristupu k nastrojum.
  Vysledkovy rozdil odrazi workflow, ne jen inteligenci modelu.
</div>

<div class="grid">""" + cards_html + """</div>

<h2 style="color:#f1f5f9;margin-bottom:.5rem">Topic coverage matrix</h2>
<table class="matrix"><thead>""" + matrix_header + """</thead><tbody>""" + matrix_rows + """</tbody></table>

<h2 style="color:#f1f5f9;margin-bottom:.5rem">Plne texty analyz</h2>
<div class="tabs">""" + tabs_html + """</div>
""" + panels_html + """

<script>
function showTab(idx) {
  document.querySelectorAll('.tab-panel').forEach(function(p,i){ p.style.display = i===idx ? 'block' : 'none'; });
  document.querySelectorAll('.tab-btn').forEach(function(b,i){ b.style.background = i===idx ? '#1d4ed8' : ''; });
}
showTab(0);
</script>
</body>
</html>"""

OUTPUT.parent.mkdir(parents=True, exist_ok=True)
OUTPUT.write_text(html, encoding="utf-8")
print(f"Report ulozen: {OUTPUT}")
print(f"  Modely: {len(entries)}, Temata: {len(TOPICS)} (15 obsah + 3 cestina)")
for e in sorted_entries:
    cz = e.get("czech", (False,False,False))
    cz_str = ("OK" if cz[0] else "NO") + "-diak " + ("OK" if cz[1] else "NO") + "-slova " + ("OK" if cz[2] else "NO") + "-cs"
    print(f"  {e['name']:25s} skore {e['score']:2}/{len(TOPICS)}  {len(e['text']):,} znaku  ({e['elapsed']})  {cz_str}")
