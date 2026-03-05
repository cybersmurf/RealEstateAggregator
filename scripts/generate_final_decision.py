#!/usr/bin/env python3
"""
Finální rozhodovací report – všechny inzeráty v kategoriích Liked/ToVisit/Visited.
Stáhne nejnovější mistral-tools analýzu pro každý inzerát a vygeneruje HTML report.
"""
import httpx
import json
import sys
import re
from datetime import datetime
from pathlib import Path

API_BASE = "http://localhost:5001"
OUTPUT = "exports/final-decision-report.html"

# VŠECHNY inzeráty v priority pořadí pro finální rozhodnutí
LISTINGS_ORDER = [
    # Visited – osobně navštíveno
    ("74b6f591-78ff-4222-9450-9535d5e2639e", "Visited"),
    ("0de2b4e6-5e83-4128-8eb4-51c74a4776c8", "Visited"),
    ("967a2865-0eb6-4642-bca9-87fc8e0ff4b8", "Visited"),
    ("14fe1165-c84f-4dcd-b5aa-ca01ae563f22", "Visited"),
    # ToVisit – naplánované prohlídky
    ("56acfea4-0c04-44c3-8ea8-b0e5c8e1d250", "ToVisit"),
    ("45cd728f-13e5-4bab-9397-fcdc22cfd318", "ToVisit"),
    ("b08f88b7-a317-420c-a0d3-f0c3132ee813", "ToVisit"),
    ("ae5745e9-b580-43a7-b262-420de5eac3f7", "ToVisit"),
    ("e9ea13a5-77f4-42b7-a540-8a4aee968371", "ToVisit"),
    # Liked – zájem, bez prohlídky
    ("7b5b50ac-6a7d-4e58-bf4b-d3846ea5e6cf", "Liked"),
    ("b410bb3f-a2f8-41d4-a47b-692045a9bba0", "Liked"),
    ("383c843d-3b15-4113-b75f-1185b933d5d2", "Liked"),
    ("ac332897-a5aa-4ed8-875b-485928ff2f0a", "Liked"),
    ("e4328a6c-268c-4289-8dba-381157b876fa", "Liked"),
    ("57a4033e-5eb3-404a-9898-a2737de990b7", "Liked"),
    ("344daecc-8b1a-4057-93a7-9fe2b14c24cc", "Liked"),
    ("6cb00624-e930-42c6-8159-7f18f9afade9", "Liked"),
]

STATUS_COLORS = {
    "Visited": {"bg": "#d1fae5", "badge": "#059669", "label": "✓ Navštíveno", "order": 1},
    "ToVisit": {"bg": "#dbeafe", "badge": "#2563eb", "label": "🚗 K návštěvě", "order": 2},
    "Liked":   {"bg": "#fef9c3", "badge": "#ca8a04", "label": "❤️ Zajímavé",  "order": 3},
}

PRICE_SIGNAL_STYLE = {
    "low":  ("🟢", "Cena nízká / výhodná",  "#15803d", "#dcfce7"),
    "fair": ("🟡", "Cena odpovídající",      "#92400e", "#fef3c7"),
    "high": ("🔴", "Cena vysoká",            "#991b1b", "#fee2e2"),
    None:   ("⚪", "Bez hodnocení ceny",     "#374151", "#f3f4f6"),
}


def fetch_listing(lid: str) -> dict:
    resp = httpx.get(f"{API_BASE}/api/listings/{lid}", timeout=30)
    resp.raise_for_status()
    return resp.json()


def fetch_analyses(lid: str) -> list:
    resp = httpx.get(f"{API_BASE}/api/listings/{lid}/analyses", timeout=30)
    resp.raise_for_status()
    return resp.json()


def pick_best_analysis(analyses: list) -> dict | None:
    if not analyses:
        return None
    # Priorita: mistral-tools > claude (desktop/local) > ostatní
    for prefix in ["local:mistral-tools/", "claude", "local:claude/"]:
        candidates = [a for a in analyses if (a.get("source") or "").startswith(prefix)]
        if candidates:
            return sorted(candidates, key=lambda a: a.get("createdAt", ""))[-1]
    # Fallback: nejnovější
    return sorted(analyses, key=lambda a: a.get("createdAt", ""))[-1]


def extract_verdict_lines(content: str) -> str:
    """Extrahuje VERDIKT sekci z analýzy (markdown)."""
    if not content:
        return ""
    # Hledáme sekci VERDIKT nebo emoji verdict
    patterns = [
        r"(?:##\s*)?(?:🏁|VERDIKT|verdikt|DOPORUČENÍ|Doporučení).*?(?=\n##|\n---|\Z)",
        r"(?:##\s+\d+\.\s*)?(?:ZÁVĚR|Závěr|CELKOVÉ|Celkové|SHRNUTÍ|Shrnutí).*?(?=\n##|\n---|\Z)",
    ]
    for pat in patterns:
        m = re.search(pat, content, re.DOTALL | re.IGNORECASE)
        if m:
            text = m.group(0).strip()
            # Zkrátit na max 1500 znaků
            if len(text) > 1500:
                text = text[:1500] + "\n…"
            return text
    return ""


def extract_first_table(content: str) -> str:
    """Extrahuje první tabulku z markdown."""
    m = re.search(r"(\|[^\n]+\|\n\|[-| :]+\|\n(?:\|[^\n]+\|\n?)+)", content)
    return m.group(0) if m else ""


def md_to_html(text: str) -> str:
    """Jednoduchá markdown→HTML konverze pro zobrazení v reportu."""
    if not text:
        return ""
    import html as htmlmod
    # Escapujeme speciální znaky, ale zachováme strukturu
    lines = text.split('\n')
    out = []
    in_table = False
    in_ul = False
    for line in lines:
        # Prázdná řádka
        if not line.strip():
            if in_table:
                out.append('</table>')
                in_table = False
            if in_ul:
                out.append('</ul>')
                in_ul = False
            out.append('<br>')
            continue

        # Tabulka (markdown)
        if line.startswith('|'):
            stripped = line.strip()
            cells = [c.strip() for c in stripped.split('|')[1:-1]]
            if all(re.match(r'^[-: ]+$', c) for c in cells):
                # Header separator přeskočíme
                continue
            if not in_table:
                out.append('<table class="md-table">')
                in_table = True
                # první řádek tabulky = hlavička
                out.append('<tr>' + ''.join(f'<th>{htmlmod.escape(c)}</th>' for c in cells) + '</tr>')
            else:
                out.append('<tr>' + ''.join(f'<td>{htmlmod.escape(c)}</td>' for c in cells) + '</tr>')
            continue

        if in_table:
            out.append('</table>')
            in_table = False

        # Nadpisy
        hm = re.match(r'^(#{1,4})\s+(.*)', line)
        if hm:
            if in_ul:
                out.append('</ul>')
                in_ul = False
            level = len(hm.group(1))
            out.append(f'<h{level+2} class="md-h{level}">{htmlmod.escape(hm.group(2))}</h{level+2}>')
            continue

        # HR
        if re.match(r'^---+$', line.strip()):
            out.append('<hr>')
            continue

        # Odrážky
        bm = re.match(r'^[-*]\s+(.*)', line)
        if bm:
            if not in_ul:
                out.append('<ul>')
                in_ul = True
            item = bm.group(1)
            # **bold** inline
            item = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', htmlmod.escape(item))
            out.append(f'<li>{item}</li>')
            continue

        if in_ul:
            out.append('</ul>')
            in_ul = False

        # Normální řádka – formátování **bold** a *italic*
        escaped = htmlmod.escape(line)
        escaped = re.sub(r'\*\*(.+?)\*\*', r'<strong>\1</strong>', escaped)
        escaped = re.sub(r'\*(.+?)\*', r'<em>\1</em>', escaped)
        out.append(f'<p>{escaped}</p>')

    if in_table:
        out.append('</table>')
    if in_ul:
        out.append('</ul>')

    return '\n'.join(out)


def build_score_badge(listing: dict) -> str:
    ps = listing.get("priceSignal")
    icon, label, color, bg = PRICE_SIGNAL_STYLE.get(ps, PRICE_SIGNAL_STYLE[None])
    return f'<span class="price-badge" style="background:{bg};color:{color}">{icon} {label}</span>'


def build_card(listing: dict, analysis: dict | None, status: str, idx: int) -> str:
    sc = STATUS_COLORS[status]
    lid = listing["id"]
    title = listing.get("title", "—")
    location = listing.get("locationText", "—")
    price_raw = listing.get("price")
    price_str = f"{price_raw/1e6:.2f} M Kč" if price_raw else "neuvedena"
    area_b = listing.get("areaBuiltUp") or 0
    area_l = listing.get("areaLand") or 0
    rooms = listing.get("disposition") or listing.get("rooms") or "—"
    condition = listing.get("condition") or "—"
    construction = listing.get("constructionType") or "—"
    source_url = listing.get("sourceUrl") or "#"
    photo_url = ""
    photos = listing.get("photos") or []
    if photos:
        p = photos[0]
        photo_url = p.get("storedUrl") or p.get("originalUrl") or ""

    has_notes = bool((listing.get("userState") or {}).get("notes", ""))
    notes_text = ((listing.get("userState") or {}).get("notes") or "")

    smart_tags = listing.get("smartTags") or []
    ai_data = listing.get("aiNormalizedData") or {}

    analysis_content = (analysis or {}).get("content", "")
    analysis_source = (analysis or {}).get("source", "—")
    analysis_date = (analysis or {}).get("createdAt", "")
    if analysis_date:
        try:
            analysis_date = datetime.fromisoformat(analysis_date.replace("Z", "+00:00")).strftime("%d.%m.%Y %H:%M")
        except Exception:
            pass

    verdict_html = md_to_html(extract_verdict_lines(analysis_content)) if analysis_content else ""
    full_analysis_html = md_to_html(analysis_content) if analysis_content else "<p><em>Analýza zatím není k dispozici.</em></p>"

    tags_html = " ".join(f'<span class="tag">{t}</span>' for t in smart_tags[:8]) if smart_tags else ""

    photosection = ""
    if photo_url:
        photosection = f'<img class="listing-photo" src="{photo_url}" alt="Foto" onerror="this.style.display=\'none\'">'

    condition_badge = f'<span class="cond-badge">{condition}</span>' if condition and condition != "—" else ""

    area_str = ""
    if area_b:
        area_str += f"{int(area_b)} m² dům"
    if area_l:
        area_str += f" / {int(area_l)} m² pozemek"

    return f"""
<div class="listing-card" id="card-{lid}" style="border-left: 4px solid {sc['badge']}">
  <div class="card-header" style="background:{sc['bg']}">
    <div class="card-header-left">
      <span class="status-badge" style="background:{sc['badge']}">{sc['label']}</span>
      {build_score_badge(listing)}
      <span class="idx-badge">#{idx}</span>
    </div>
    <div class="card-header-right">
      <a href="{source_url}" target="_blank" class="source-link">🔗 Inzerát</a>
    </div>
  </div>

  <div class="card-body">
    <div class="card-left">
      {photosection}
      <div class="listing-meta">
        <h2 class="listing-title">{title}</h2>
        <div class="listing-loc">📍 {location}</div>
        <div class="listing-price">💰 <strong>{price_str}</strong></div>
        {'<div class="listing-area">📐 ' + area_str + '</div>' if area_str else ''}
        {'<div class="listing-rooms">🛏 ' + str(rooms) + '</div>' if rooms and rooms != '—' else ''}
        {condition_badge}
        {('<div class="tags">' + tags_html + '</div>') if tags_html else ''}
      </div>
    </div>

    <div class="card-right">
      {'<details class="notes-section"><summary>📋 Zápis z prohlídky (' + str(len(notes_text)) + ' znaků)</summary><div class="notes-content">' + notes_text.replace('\n','<br>') + '</div></details>' if has_notes else ''}

      {'<div class="verdict-box">' + verdict_html + '</div>' if verdict_html else ''}

      <details class="analysis-details">
        <summary>📄 Plná analýza 
          <small style="color:#6b7280">({analysis_source} · {analysis_date} · {len(analysis_content):,} znaků)</small>
        </summary>
        <div class="analysis-content">{full_analysis_html}</div>
      </details>
    </div>
  </div>
</div>"""


def generate_html(cards_html: str, listings_data: list) -> str:
    date_str = datetime.now().strftime("%d.%m.%Y %H:%M")
    total = len(listings_data)
    visited_count = sum(1 for _, _, s in listings_data if s == "Visited")
    tovisit_count = sum(1 for _, _, s in listings_data if s == "ToVisit")
    liked_count = sum(1 for _, _, s in listings_data if s == "Liked")

    return f"""<!DOCTYPE html>
<html lang="cs">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Finální rozhodovací report – {date_str}</title>
  <style>
    * {{ box-sizing: border-box; margin: 0; padding: 0; }}
    body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #f8fafc; color: #1e293b; line-height: 1.6; }}
    .header {{ background: linear-gradient(135deg, #1e293b, #334155); color: white; padding: 32px 40px; }}
    .header h1 {{ font-size: 1.8rem; margin-bottom: 8px; }}
    .header .subtitle {{ opacity: 0.8; font-size: 0.95rem; }}
    .stats {{ display: flex; gap: 16px; margin-top: 16px; flex-wrap: wrap; }}
    .stat-chip {{ background: rgba(255,255,255,0.15); border-radius: 20px; padding: 4px 14px; font-size: 0.85rem; }}
    .toc {{ background: white; border-bottom: 1px solid #e2e8f0; padding: 16px 40px; }}
    .toc h3 {{ font-size: 0.85rem; text-transform: uppercase; letter-spacing: 0.05em; color: #64748b; margin-bottom: 10px; }}
    .toc-list {{ display: flex; flex-wrap: wrap; gap: 8px; list-style: none; }}
    .toc-list a {{ text-decoration: none; color: #3b82f6; font-size: 0.85rem; padding: 3px 8px; border-radius: 4px; border: 1px solid #bfdbfe; }}
    .toc-list a:hover {{ background: #eff6ff; }}
    .content {{ max-width: 1200px; margin: 0 auto; padding: 24px 20px; }}
    .section-title {{ font-size: 1.1rem; font-weight: 600; color: #475569; margin: 28px 0 12px; padding-bottom: 6px; border-bottom: 2px solid #e2e8f0; display: flex; align-items: center; gap: 8px; }}
    .listing-card {{ background: white; border-radius: 12px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); margin-bottom: 20px; overflow: hidden; }}
    .card-header {{ display: flex; justify-content: space-between; align-items: center; padding: 10px 16px; }}
    .card-header-left {{ display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }}
    .status-badge {{ color: white; padding: 3px 10px; border-radius: 12px; font-size: 0.78rem; font-weight: 600; }}
    .price-badge {{ padding: 3px 10px; border-radius: 12px; font-size: 0.78rem; font-weight: 600; }}
    .idx-badge {{ background: #e2e8f0; color: #475569; padding: 3px 8px; border-radius: 12px; font-size: 0.78rem; }}
    .source-link {{ color: #3b82f6; text-decoration: none; font-size: 0.85rem; }}
    .source-link:hover {{ text-decoration: underline; }}
    .card-body {{ display: flex; gap: 20px; padding: 16px; }}
    .card-left {{ flex: 0 0 280px; }}
    .listing-photo {{ width: 100%; height: 180px; object-fit: cover; border-radius: 8px; margin-bottom: 12px; }}
    .listing-meta {{ font-size: 0.88rem; }}
    .listing-title {{ font-size: 1rem; font-weight: 600; margin-bottom: 8px; line-height: 1.4; color: #1e293b; }}
    .listing-loc, .listing-price, .listing-area, .listing-rooms {{ margin-bottom: 4px; color: #374151; }}
    .listing-price {{ font-size: 1rem; margin-top: 6px; }}
    .cond-badge {{ display: inline-block; background: #f1f5f9; color: #475569; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; margin: 6px 0; }}
    .tags {{ margin-top: 8px; display: flex; flex-wrap: wrap; gap: 4px; }}
    .tag {{ background: #eff6ff; color: #1d4ed8; padding: 2px 7px; border-radius: 4px; font-size: 0.75rem; }}
    .card-right {{ flex: 1; min-width: 0; }}
    .notes-section {{ margin-bottom: 12px; background: #fefce8; border: 1px solid #fde68a; border-radius: 8px; overflow: hidden; }}
    .notes-section summary {{ padding: 8px 12px; cursor: pointer; font-weight: 600; font-size: 0.88rem; color: #92400e; }}
    .notes-content {{ padding: 10px 12px; font-size: 0.85rem; color: #451a03; white-space: pre-wrap; }}
    .verdict-box {{ background: #f0fdf4; border: 1px solid #86efac; border-radius: 8px; padding: 12px 14px; margin-bottom: 12px; font-size: 0.88rem; }}
    .verdict-box h4, .verdict-box h5, .verdict-box h6 {{ font-size: 0.9rem; margin-bottom: 6px; color: #14532d; }}
    .analysis-details {{ border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden; }}
    .analysis-details summary {{ padding: 10px 14px; cursor: pointer; background: #f8fafc; font-size: 0.88rem; font-weight: 600; color: #475569; }}
    .analysis-details summary:hover {{ background: #f1f5f9; }}
    .analysis-content {{ padding: 14px; font-size: 0.83rem; max-height: 500px; overflow-y: auto; }}
    .analysis-content p {{ margin-bottom: 6px; }}
    .analysis-content h3, .analysis-content h4, .analysis-content h5 {{ margin: 10px 0 4px; color: #1e293b; font-size: 0.9rem; }}
    .analysis-content ul {{ padding-left: 20px; margin-bottom: 6px; }}
    .analysis-content li {{ margin-bottom: 3px; }}
    table.md-table {{ border-collapse: collapse; width: 100%; margin: 8px 0; font-size: 0.8rem; }}
    table.md-table th, table.md-table td {{ border: 1px solid #e2e8f0; padding: 5px 8px; text-align: left; }}
    table.md-table th {{ background: #f1f5f9; font-weight: 600; }}
    table.md-table tr:nth-child(even) td {{ background: #f8fafc; }}
    .no-analysis {{ color: #9ca3af; font-style: italic; font-size: 0.85rem; padding: 16px; }}
    @media (max-width: 768px) {{
      .card-body {{ flex-direction: column; }}
      .card-left {{ flex: none; width: 100%; }}
    }}
  </style>
</head>
<body>
<div class="header">
  <h1>🏡 Finální rozhodovací report</h1>
  <div class="subtitle">Generováno: {date_str} | Model: mistral-tools/mistral-large-latest (18/19 bodů)</div>
  <div class="stats">
    <span class="stat-chip">📊 Celkem: {total} inzerátů</span>
    <span class="stat-chip">✓ Navštíveno: {visited_count}</span>
    <span class="stat-chip">🚗 K návštěvě: {tovisit_count}</span>
    <span class="stat-chip">❤️ Zajímavé: {liked_count}</span>
  </div>
</div>

<nav class="toc">
  <h3>Rychlá navigace</h3>
  <ul class="toc-list">
    {"".join(f'<li><a href="#card-{lid}">{name[:30]}</a></li>' for lid, name, status in listings_data)}
  </ul>
</nav>

<div class="content">
  <div class="section-title">✓ Navštívené nemovitosti</div>
  {cards_html[0]}

  <div class="section-title">🚗 K návštěvě</div>
  {cards_html[1]}

  <div class="section-title">❤️ Zajímavé (bez prohlídky)</div>
  {cards_html[2]}
</div>
</body>
</html>"""


def main():
    print(f"[{datetime.now():%H:%M:%S}] Generuji finální rozhodovací report...", flush=True)

    visited_cards = []
    tovisit_cards = []
    liked_cards = []
    listings_data = []

    for idx, (lid, expected_status) in enumerate(LISTINGS_ORDER, 1):
        print(f"  [{idx:02d}/17] {lid[:8]}...", end=" ", flush=True)
        try:
            listing = fetch_listing(lid)
            analyses = fetch_analyses(lid)
            analysis = pick_best_analysis(analyses)

            # Zjisti skutečný status z user_state
            user_state = listing.get("userState") or {}
            status = user_state.get("status") or expected_status

            name = listing.get("locationText") or listing.get("title") or lid
            listings_data.append((lid, name, status))

            card = build_card(listing, analysis, status, idx)
            if status == "Visited":
                visited_cards.append(card)
            elif status == "ToVisit":
                tovisit_cards.append(card)
            else:
                liked_cards.append(card)

            a_source = (analysis or {}).get("source", "—")
            a_len = len((analysis or {}).get("content", "") or "")
            print(f"✓ {name[:25]:25} | {a_source[:30]} | {a_len:,} znaků")
        except Exception as e:
            print(f"❌ CHYBA: {e}")
            listings_data.append((lid, lid[:8], expected_status))

    cards_html = [
        "\n".join(visited_cards) or '<p class="no-analysis">Žádné navštívené inzeráty.</p>',
        "\n".join(tovisit_cards) or '<p class="no-analysis">Žádné inzeráty k návštěvě.</p>',
        "\n".join(liked_cards)   or '<p class="no-analysis">Žádné zajímavé inzeráty.</p>',
    ]

    html = generate_html(cards_html, listings_data)

    output_path = Path(__file__).parent.parent / OUTPUT
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(html, encoding="utf-8")
    print(f"\n[{datetime.now():%H:%M:%S}] Report uložen: {output_path}", flush=True)
    print(f"Otevřít: open '{output_path}'")


if __name__ == "__main__":
    main()
