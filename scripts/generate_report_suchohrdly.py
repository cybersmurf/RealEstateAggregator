#!/usr/bin/env python3
"""Benchmark report – Suchohrdly 6+2, 182 m², po prohlídce 25.2.2026.
   Listing ID: 14fe1165-c84f-4dcd-b5aa-ca01ae563f22
   Reference: Analýza V3 z Claude Desktop (claude-desktop-v3)
"""
import subprocess, re, json, sys, unicodedata
from datetime import datetime
from pathlib import Path

LISTING_ID = "14fe1165-c84f-4dcd-b5aa-ca01ae563f22"
OUTPUT = Path("/Users/petrsramek/Projects/RealEstateAggregator/exports/model-comparison-suchohrdly-v1.html")


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
# Tuple: (zobrazovane_jmeno, db_source, kind, color, popis_pro_uzivatele)
MODELS = [
    # Referenční analýza z Claude Desktop (po prohlídÎ, s MCP tools)
    ("Claude Desktop V3 (ref)", "claude-desktop-v3",                          "cloud", "#059669", "Anthropic · Claude Desktop s MCP tools + zápis z prohlídky (referenční analýza V3)"),
    # Cloud modely
    ("Claude Opus 4.6",         "local:claude/claude-opus-4-6",               "cloud", "#4f46e5", "Anthropic · nejvýkonnější model řady Claude 4"),
    ("Claude Sonnet 4.6",       "local:claude/claude-sonnet-4-6",             "cloud", "#7c3aed", "Anthropic · vyvážený výkon/cena, Claude 4 střed"),
    ("Claude Sonnet 4.5",       "local:claude/claude-sonnet-4-5-20250929",    "cloud", "#6d28d9", "Anthropic · Claude 3.7 Sonnet (září 2025)"),
    ("Claude 3 Haiku",          "local:claude/claude-3-haiku-20240307",       "cloud", "#c084fc", "Anthropic · nejrychlejší/nejlevnější Claude 3"),
    ("Mistral Large",           "local:mistral/mistral-large-latest",          "cloud", "#f97316", "Mistral AI · vlajkový model, ~123B parametrů"),
    ("DeepSeek V3.1 671B",      "local:ollama-cloud/deepseek-v3.1:671b",      "cloud", "#0891b2", "DeepSeek · 671B MoE, čínský open-source, top tier"),
    ("Gemma3 27B",              "local:ollama-cloud/gemma3:27b",               "cloud", "#0d9488", "Google · Gemma 3, 27B parametrů, open-weights"),
    # Groq
    ("Groq Llama 3.3 70B",      "local:groq/llama-3.3-70b-versatile",         "cloud", "#dc2626", "Meta · Llama 3.3 70B přes Groq API (plain prompt, bez nástrojů)"),
    ("Groq + Tools",            "local:groq-tools/llama-3.3-70b-versatile",   "cloud", "#b91c1c", "Meta · Llama 3.3 70B + Groq function calling (get_listing + photos + cadastre)"),
    # Lokální modely
    ("Mistral S3.2 24B",        "local:mistral-small3.2:24b",                 "local", "#eab308", "Mistral AI · Small 3.2, 24B, lokálně přes Ollama"),
    ("Qwen2.5 14B",             "qwen-local",                                  "local", "#16a34a", "Alibaba · Qwen 2.5, 14B parametrů, lokálně přes Ollama"),
    ("Qwen3.5 9B",              "local:qwen3.5:9b",                           "local", "#6b7280", "Alibaba · Qwen 3.5, 9B parametrů, nejmenší testovaný model"),
]

# ── Načti texty + časy z DB ─────────────────────────────────────────────────
entries = []
for name, src, kind, color, desc in MODELS:
    text = psql(
        f"SELECT content FROM re_realestate.listing_analyses "
        f"WHERE listing_id='{LISTING_ID}' AND source='{src}' "
        f"ORDER BY created_at DESC LIMIT 1"
    )
    if not text:
        print(f"Přeskakuji {name} – analýza v DB nenalezena", file=sys.stderr)
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
                    "elapsed": elapsed, "text": text, "desc": desc})

if not entries:
    print("CHYBA: Žádné analýzy v DB pro tuto nemovitost!", file=sys.stderr)
    sys.exit(1)

# ── Témata specifická pro Suchohrdly ──────────────────────────────────────
# Témata jsou odvozena z reálné prohlídky a analýzy V3 z Claude Desktop
TOPICS = [
    # ─ Právní / procesní ─────────────────────────────────────────────────
    ("Dědické řízení / soud",              r"dedick|dedictv|soud.{0,30}schval|dedic.{0,20}rizeni|dedic"),
    ("Odložené převzetí (+2-3 měsíce)",    r"2.3 mesic|mesice.{0,20}prevod|mesice.{0,20}preved|soud.{0,30}mesic|dedic.{0,30}mesic|prevod.{0,30}delsi"),
    # ─ Konkrétní závady z prohlídky ──────────────────────────────────────
    ("Parkování neřešené",                 r"parkov|stani.{0,20}problem|stani.{0,20}chybi|parkoviste"),
    ("Mostek / povodí zákaz",              r"mostek|prehaz|povodi.{0,20}zakaz|pevne.{0,20}stavb|urcite.{0,20}odstranit|docasn.{0,20}prejezd"),
    ("Chybí kuchyňská linka",              r"kuchyn.{0,10}link|link.{0,10}chybi|kuchyn.{0,10}chybi|nova linka"),
    ("Oprava střechy dílny",               r"strich.{0,15}diln|diln.{0,15}strich|diln.{0,15}oprav|ipa.{0,20}foli|strich.{0,20}oprav"),
    ("Zateplení – tloušťka neověřena",     r"zateplen|tloustka.{0,30}neover|zateplen.{0,20}tloustk|fasad.{0,20}zatep"),
    # ─ Pozitiva / hodnota ─────────────────────────────────────────────────
    ("Dvě bytové jednotky / dvougenerační",r"dve.{0,15}jednotk|dvougenerac|dve byto|podnajem.{0,20}podkrovi|2+1.{0,20}podkrovi|pronajem.{0,10}podkrovi"),
    ("Sklep 55 m² – přidaná hodnota",      r"sklep.{0,20}55|55.{0,20}sklep|sklep.{0,20}velk|velk.{0,20}sklep|mistnost.{0,20}sklep"),
    # ─ Finance ────────────────────────────────────────────────────────────
    ("Finanční přehled / celkové náklady", r"celkove.{0,20}naklad|celkem.{0,20}naklad|celkova.{0,20}invest|150.{0,10}000.{0,30}Kc|410.{0,10}000"),
    ("Yield / výnos z pronájmu",           r"yield|vynos|pronajem.{0,30}mesic|najem.{0,20}\d|8.000|12.000|najem.{0,20}podkrovi"),
    ("ROI / návratnost investice",         r"navratnost|ROI|return"),
    ("Srovnání Kč/m² s trhem",             r"trzni.{0,50}Kc|trzne.{0,50}Kc|prumer.{0,20}Kc/m|trzni.{0,30}obvyklou|36.000|38.000|38.400|40.000"),
    # ─ Due Diligence ──────────────────────────────────────────────────────
    ("Otázky / Due Diligence",             r"otazky|co.{0,10}zeptat|Due Diligence|doporuc"),
    ("PENB chybějící",                     r"PENB.{0,30}chybi|chybi.{0,30}PENB|energetick.{0,20}prukaz"),
    # ─ Bodovací systém ────────────────────────────────────────────────────
    ("Bodovací tabulka X/5",               r"\d\s*/\s*5\b|4\.2\s*/\s*5|SKORE.*bod|celkov.{0,10}hodnoceni"),
    # ─ Kvalita češtiny ────────────────────────────────────────────────────
    ("Diakritika > 3 %",                   None),
    ("Česká slovní zásoba",                None),
    ("Neodpovídá anglicky",                None),
]

TOPIC_LABELS = [
    "⚖ Dědické řízení / soud",
    "🗓 Odložené převzetí (+2–3 měs.)",
    "🚗 Parkování neřešené",
    "🌊 Mostek / povodí zákaz",
    "🍳 Chybí kuchyňská linka",
    "🔧 Oprava střechy dílny",
    "🏠 Zateplení – tloušťka neověřena",
    "👨‍👩‍👧 Dvě b. jednotky / dvougenerační",
    "📦 Sklep 55 m² – přidaná hodnota",
    "💰 Finanční přehled / celk. náklady",
    "📊 Yield / výnos z pronájmu",
    "📈 ROI / návratnost investice",
    "📌 Srovnání Kč/m² s trhem",
    "❓ Otázky / Due Diligence",
    "🔴 PENB chybějící",
    "🏆 Bodovací tabulka X/5",
    "🇨🇿 Diakritika > 3 %",
    "🗣 Česká slovní zásoba",
    "🚫 Neodpovídá anglicky",
]

# ── Pomocné funkce pro češtinu ──────────────────────────────────────────────
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
    alpha = [c for c in text if c.isalpha()]
    cz_ratio = sum(1 for c in alpha if c in CZ_DIACRITICS) / max(len(alpha), 1)
    cz_words  = sum(1 for w in CZ_REALTY_WORDS if w.lower() in text.lower())
    return (cz_ratio > 0.03, cz_words >= 4, not bool(EN_SIGNAL.search(text)))


# ── Spočítej skóre ──────────────────────────────────────────────────────────
for e in entries:
    cs = czech_score(e["text"])
    e["czech"] = cs
    text_norm = strip_diacritics(e["text"])
    n_content = len([t for t in TOPICS if t[1] is not None])
    e["score"] = (
        sum(1 for _, pat in TOPICS if pat is not None
            and re.search(pat, text_norm, re.IGNORECASE))
        + sum(cs)
    )
    e["topics"] = [
        (re.search(pat, text_norm, re.IGNORECASE) is not None) if pat is not None else csval
        for (_, pat), csval in zip(TOPICS, [None] * n_content + list(cs))
    ]

max_score = max(e["score"] for e in entries)
n_topics = len(TOPICS)


# ── Markdown to HTML (simple) ─────────────────────────────────────────────
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
            tag = "td"
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

cards_html = ""
sorted_entries = sorted(entries, key=lambda x: x["score"], reverse=True)
for rank, e in enumerate(sorted_entries, 1):
    pct = e["score"] / n_topics * 100
    crown = "🥇 " if rank == 1 else ("🥈 " if rank == 2 else ("🥉 " if rank == 3 else ""))
    ref_badge = ' <span style="background:#064e3b;color:#6ee7b7;padding:.1rem .4rem;border-radius:.25rem;font-size:.7rem;font-weight:700">REF</span>' if e["src"] == "claude-desktop-v3" else ""
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
        <span class="model-name">{crown}{e['name']}</span>
        {ref_badge}
        {kind_badge}
        <span class="elapsed-badge">time: {e['elapsed']}</span>
        <div class="model-desc">{e.get('desc','')}</div>
        <div class="model-src">{e['src']}</div>
        <div class="cz-row">{cz_flags}</div>
      </div>
      <div class="score-bar-wrap">
        <div class="score-bar" style="width:{pct:.0f}%; background:{e['color']}"></div>
      </div>
      <span class="score-val">{e['score']}/{n_topics}</span>
      <span class="char-count">{len(e['text']):,} znaku</span>
    </div>"""

matrix_header = "<tr><th>Téma</th>" + "".join(f'<th title="{e["name"]}">{e["name"][:12]}</th>' for e in entries) + "</tr>"
matrix_rows = ""
for t_idx, tlabel in enumerate(TOPIC_LABELS):
    row = f"<tr><td>{tlabel}</td>"
    for e in entries:
        ok = e["topics"][t_idx]
        row += f'<td class="{"hit" if ok else "miss"}">{"✓" if ok else "–"}</td>'
    row += "</tr>"
    matrix_rows += row

now = datetime.now().strftime("%d. %m. %Y %H:%M")
n_content = len([t for t in TOPICS if t[1] is not None])

html = f"""<!DOCTYPE html>
<html lang="cs">
<head>
<meta charset="UTF-8">
<title>Benchmark Suchohrdly 6+2 – {now}</title>
<style>
  body{{font-family:system-ui,sans-serif;background:#0f172a;color:#e2e8f0;margin:0;padding:2rem}}
  h1{{color:#f1f5f9;font-size:1.6rem;margin-bottom:.2rem}}
  .sub{{color:#94a3b8;font-size:.9rem;margin-bottom:2rem}}
  .grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:1rem;margin-bottom:2rem}}
  .card{{background:#1e293b;border-radius:.75rem;padding:1rem}}
  .card-header{{display:flex;align-items:center;flex-wrap:wrap;gap:.4rem;margin-bottom:.6rem}}
  .rank{{color:#94a3b8;font-size:.85rem;min-width:2rem}}
  .model-name{{font-weight:700;font-size:1rem}}
  .badge{{padding:.15rem .45rem;border-radius:.25rem;font-size:.7rem;font-weight:700}}
  .badge-cloud{{background:#1d4ed8;color:#bfdbfe}}
  .badge-local{{background:#166534;color:#bbf7d0}}
  .elapsed-badge{{font-size:.75rem;color:#94a3b8;margin-left:auto}}
  .model-desc{{width:100%;font-size:.72rem;color:#64748b;margin-top:.15rem;font-style:italic}}
  .model-src{{width:100%;font-size:.68rem;color:#475569;font-family:monospace;margin-top:.1rem}}
  .cz-row{{width:100%;font-size:.8rem;letter-spacing:.1rem;margin-top:.2rem}}
  .cz.ok{{color:#4ade80;font-weight:700}}
  .cz.fail{{color:#f87171;opacity:.7}}
  .score-bar-wrap{{background:#334155;border-radius:4px;height:8px;margin:.4rem 0}}
  .score-bar{{height:8px;border-radius:4px}}
  .score-val{{font-size:1.3rem;font-weight:700}}
  .char-count{{font-size:.75rem;color:#94a3b8;margin-left:.8rem}}
  .matrix{{width:100%;border-collapse:collapse;font-size:.78rem;margin-bottom:2rem;overflow-x:auto;display:block}}
  .matrix th,.matrix td{{padding:.3rem .5rem;border:1px solid #334155;white-space:nowrap}}
  .matrix th{{background:#1e293b;color:#94a3b8}}
  .hit{{background:#052e16;color:#4ade80;text-align:center}}
  .miss{{background:#1a0a0a;color:#f87171;text-align:center}}
  .md-table{{border-collapse:collapse;width:100%;margin:.5rem 0}}
  .md-table th,.md-table td{{border:1px solid #334155;padding:.3rem .6rem;text-align:left}}
  .md-table th{{background:#1e293b}}
  .tabs{{display:flex;flex-wrap:wrap;gap:.4rem;margin-bottom:1rem}}
  .tab-btn{{background:#1e293b;border:1px solid #334155;color:#94a3b8;padding:.4rem .9rem;border-radius:.4rem;cursor:pointer;font-size:.85rem}}
  .tab-btn:hover{{background:#334155;color:#f1f5f9}}
  .tab-panel h1,.tab-panel h2,.tab-panel h3{{color:#93c5fd}}
  .tab-panel p{{line-height:1.6;color:#cbd5e1}}
  .tab-panel ul{{padding-left:1.2rem;color:#cbd5e1}}
  .note{{background:#1e3a5f;border-left:3px solid #3b82f6;padding:.7rem 1rem;border-radius:0 .5rem .5rem 0;margin-bottom:1.5rem;font-size:.85rem;color:#93c5fd}}
  .ref-note{{background:#064e3b;border-left:3px solid #10b981;padding:.7rem 1rem;border-radius:0 .5rem .5rem 0;margin-bottom:1.5rem;font-size:.85rem;color:#6ee7b7}}
</style>
</head>
<body>
<h1>Model Benchmark – Suchohrdly 6+2 · 182 m² · 6 990 000 Kč</h1>
<div class="sub">Generováno {now} · {len(entries)} modelů · {n_topics} hodnoticích témat ({n_content} obsah + 3 čeština) · Listing ID: {LISTING_ID}</div>

<div class="ref-note">
  📋 <strong>Referenční analýza:</strong> Claude Desktop V3 — psáno po osobní prohlídce 25. 2. 2026, s přístupem k MCP tools (get_listing, get_inspection_photos, zápis z prohlídky).
  Dům patří do <strong>dědického řízení</strong> — prodej schvaluje soud (+2–3 měsíce). Klíčové závady: parkování, mostek, chybí kuchyňská linka.
</div>

<div class="note">
  ⚠️ <strong>Metodika:</strong> Ostatní modely dostaly <strong>jednorázový textový prompt</strong> bez přístupu k fotkám prohlídky a zápisu z osobní návštěvy.
  Skóre měří pokrytí témat relevantních pro <em>tento konkrétní inzerát + prohlídku</em> — ne obecnou kvalitu analýzy.
</div>

<details style="margin-bottom:1.5rem">
<summary style="cursor:pointer;color:#94a3b8;font-size:.85rem;padding:.4rem 0">📖 Legenda – co znamenají jednotlivé modely a odznaky?</summary>
<div style="background:#1e293b;border-radius:.5rem;padding:1rem;margin-top:.5rem;font-size:.82rem;color:#94a3b8;line-height:1.8">
  <strong style="color:#e2e8f0">Odznaky na kartě:</strong><br>
  <span style="background:#064e3b;color:#6ee7b7;padding:.1rem .4rem;border-radius:.25rem;font-size:.75rem;font-weight:700">REF</span> = referenční analýza (Claude Desktop s MCP tools po osobní prohlídce) &nbsp;·&nbsp;
  <span style="background:#1d4ed8;color:#bfdbfe;padding:.1rem .4rem;border-radius:.25rem;font-size:.75rem;font-weight:700">CLOUD</span> = vzdálené API (platí se za tokeny) &nbsp;·&nbsp;
  <span style="background:#166534;color:#bbf7d0;padding:.1rem .4rem;border-radius:.25rem;font-size:.75rem;font-weight:700">LOCAL</span> = lokálně přes Ollama (zdarma, pomalejší)<br><br>
  <strong style="color:#e2e8f0">Modely: co jsou zač</strong><br>
  <b>Claude Desktop V3 (REF)</b> – referenční analýza psaná po osobní prohlídce 25.2. s přístupem k fotkám + zápisu z návštěvy<br>
  <b>Anthropic Claude 3 Haiku</b> – nejrychlejší a nejlevnější Claude, základní tier<br>
  <b>Anthropic Claude Sonnet 4.5 / 4.6</b> – střední tier Claude 4, vyvážený výkon/cena<br>
  <b>Anthropic Claude Opus 4.6</b> – nejvýkonnější Claude 4, nejdražší<br>
  <b>Mistral Large</b> – vlajkový model Mistral AI, přibližně 123B parametrů<br>
  <b>DeepSeek V3.1 671B</b> – čínský open-source gigant, 671B parametrů (MoE architektura)<br>
  <b>Gemma3 27B</b> – Google open-weights model, 27B parametrů<br>
  <b>Mistral Small 3.2 24B</b> – menší lokální Mistral, 24B parametrů<br>
  <b>Groq Llama 3.3 70B (plain)</b> – Meta Llama 3.3 70B přes Groq API, jednorázový textový prompt<br>
  <b>Groq + Tools</b> – <em>stejný model</em> (Llama 3.3 70B), ale s vícekrokovým function callingem; model sám načte data z DB<br>
  <b>Qwen 2.5 14B / Qwen 3.5 9B</b> – Alibaba open-source modely, nejmenší testované<br><br>
  <strong style="color:#e2e8f0">Proč jsou Groq modely rychlejší?</strong> Groq používá vlastní LPU čipy optimalizované pro inferenci (ne GPU) → ~10× rychlejší než standardní GPU inference.
</div>
</details>

<div class="grid">{cards_html}</div>

<h2 style="color:#f1f5f9;margin-bottom:.5rem">Topic coverage matrix</h2>
<table class="matrix"><thead>{matrix_header}</thead><tbody>{matrix_rows}</tbody></table>

<h2 style="color:#f1f5f9;margin-bottom:.5rem">Plné texty analýz</h2>
<div class="tabs">{tabs_html}</div>
{panels_html}

<script>
function showTab(idx) {{
  document.querySelectorAll('.tab-panel').forEach(function(p,i){{ p.style.display = i===idx ? 'block' : 'none'; }});
  document.querySelectorAll('.tab-btn').forEach(function(b,i){{ b.style.background = i===idx ? '#1d4ed8' : ''; }});
}}
showTab(0);
</script>
</body>
</html>"""

OUTPUT.parent.mkdir(parents=True, exist_ok=True)
OUTPUT.write_text(html, encoding="utf-8")
print(f"Report uložen: {OUTPUT}")
print(f"  Modely: {len(entries)}, Témata: {n_topics} ({n_content} obsah + 3 čeština)")
for e in sorted_entries:
    cz = e.get("czech", (False, False, False))
    cz_str = ("OK" if cz[0] else "NO") + "-diak " + ("OK" if cz[1] else "NO") + "-slova " + ("OK" if cz[2] else "NO") + "-cs"
    print(f"  {e['name']:28s} skóre {e['score']:2}/{n_topics}  {len(e['text']):,} znaků  ({e['elapsed']})  {cz_str}")
